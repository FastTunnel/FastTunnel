// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FastTunnel.Core.Config;
using FastTunnel.Core.P2P;
using FastTunnel.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Security;

/// <summary>
/// 入网设备台账：管理员签发一次性入网码，客户端兑换成自己的长期凭据。
/// 台账只存凭据指纹，明文 token 仅在兑换响应里出现一次，任何时刻都不落盘。
/// 单机 JSON 快照持久化，与 <see cref="BanRegistry"/> 同一套路；多实例部署需换共享存储。
/// </summary>
public sealed class PeerRegistry
{
    private const int MaxPendingTickets = 64;

    private static JsonSerializerOptions JsonOptions => AppJsonContext.Default.Options;

    private readonly ILogger<PeerRegistry> logger;
    private readonly IOptionsMonitor<DefaultServerConfig>? options;
    private readonly string? stateFile;
    private readonly int maxPeersOverride;
    private readonly object gate = new();
    private readonly Dictionary<string, EnrollTicket> tickets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private PeerSnapshot snapshot = PeerSnapshot.Empty;
    private DateTime? lastWriteUtc;

    public PeerRegistry(ILogger<PeerRegistry> logger, IOptionsMonitor<DefaultServerConfig> options)
        : this(logger, options?.CurrentValue?.P2PEnrollment?.StateFile)
    {
        this.options = options;
    }

    /// <summary>测试用：stateFile 传 null / 空字符串关闭持久化，maxPeers 传 0 用默认上限。</summary>
    public PeerRegistry(ILogger<PeerRegistry> logger, string? stateFile, int maxPeers = 0)
    {
        this.logger = logger;
        this.stateFile = string.IsNullOrWhiteSpace(stateFile) ? null : Path.GetFullPath(stateFile);
        maxPeersOverride = maxPeers;
        Load();
    }

    public IReadOnlyList<PeerRecord> Peers => snapshot.Peers;

    /// <summary>凭据快照。每次台账变更换成新实例，<see cref="ClientTokenDirectory"/> 按引用比较重建。</summary>
    public PeerSnapshot Current => Volatile.Read(ref snapshot);

    /// <summary>台账文件绝对路径；未启用持久化时为 null。CLI 用它提醒工作目录是否选错。</summary>
    public string? StateFilePath => stateFile;

    private int MaxPeers => maxPeersOverride > 0 ? maxPeersOverride : options?.CurrentValue?.P2PEnrollment?.MaxPeers ?? 512;

    /// <summary>签发一次性入网码，顺带清掉过期票据。</summary>
    public IssuedTicket CreateTicket(string? key, string? name, int minutes)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("未配置 FastTunnel:P2PInviteKey，无法签加入网码");

        var ticket = new EnrollTicket
        {
            Id = Guid.NewGuid(),
            Name = Normalize(name),
            CreatedAt = DateTimeOffset.UtcNow,
            Expires = DateTimeOffset.UtcNow.AddMinutes(minutes),
        };
        var code = PeerEnrollment.Create(key!, ticket.Id, minutes);
        ticket.CodeFingerprint = PeerEnrollment.Fingerprint(code);

        lock (gate)
        {
            TrimTickets();
            if (tickets.Count >= MaxPendingTickets)
                throw new InvalidOperationException("待用入网码数量已达上限，请先撤销不再使用的码");

            tickets[ticket.Id.ToString("N")] = ticket;
            Save();
        }

        logger.LogInformation("已签加入网码：票据 {Ticket} 设备名 {Name} 截止 {Expires:yyyy-MM-dd HH:mm} UTC",
            ticket.Id, ticket.Name.Length == 0 ? "(由客户端主机名决定)" : ticket.Name, ticket.Expires);
        return new IssuedTicket(ticket.Id, code, ticket.Expires);
    }

    /// <summary>
    /// 兑换入网码。校验签名、有效期、是否已用；成功后登记设备并生成一条新凭据。
    /// 返回的明文 token 只在这一次响应里出现，台账里只有它的指纹。
    /// </summary>
    public PeerCredential? Redeem(string? key, string? code, string? hostname)
    {
        if (string.IsNullOrEmpty(code))
            return null;

        var issued = PeerEnrollment.Read(code, key, DateTimeOffset.UtcNow);
        if (issued == null)
            return null;

        var lookup = issued.Value.Ticket.ToString("N");
        lock (gate)
        {
            if (!tickets.TryGetValue(lookup, out var ticket) || ticket.UsedBy != null
                || ticket.Expires <= DateTimeOffset.UtcNow
                || !string.Equals(ticket.CodeFingerprint, PeerEnrollment.Fingerprint(code), StringComparison.Ordinal))
                return null;

            if (snapshot.ById.Count >= MaxPeers)
            {
                logger.LogWarning("入网设备数已达上限 {Max}，拒绝兑换票据 {Ticket}", MaxPeers, ticket.Id);
                return null;
            }

            var requested = ticket.Name.Length == 0 ? Normalize(hostname) : ticket.Name;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var record = new PeerRecord
            {
                Id = AllocateId(requested),
                Name = requested,
                TokenFingerprint = BanRegistry.Fingerprint(token),
                EnrolledAt = DateTimeOffset.UtcNow,
            };
            lastSeen[record.Id] = record.EnrolledAt;

            var peers = snapshot.Peers.ToList();
            peers.Add(record);
            Volatile.Write(ref snapshot, PeerSnapshot.Create(peers));

            tickets.Remove(lookup);
            Save();

            logger.LogInformation("设备入网成功：主体 {Id} 票据 {Ticket} 来源 {Remote}", record.Id, ticket.Id, hostname);
            return new PeerCredential(record.Id, record.Name, token);
        }
    }

    /// <summary>吊销设备：凭据快照立即失效，进行中的 P2P 会话在下一次授权复检时关闭。</summary>
    public bool Revoke(string id) => SetEnabled(id, false);

    /// <summary>恢复一台已吊销设备，凭据不变。</summary>
    public bool Restore(string id) => SetEnabled(id, true);

    /// <summary>彻底删除设备记录。</summary>
    public bool Remove(string id)
    {
        lock (gate)
        {
            var key = Normalize(id);
            if (!snapshot.ById.ContainsKey(key))
                return false;

            Volatile.Write(ref snapshot, PeerSnapshot.Create(snapshot.Peers.Where(x => x.Id != key).ToList()));
            lastSeen.TryRemove(key, out _);
            Save();
            return true;
        }
    }

    /// <summary>记一次上线。只改内存，随下一次结构性变更一起落盘，避免连接风暴反复写文件。</summary>
    public void Touch(string? id)
    {
        var key = Normalize(id);
        if (key.Length == 0 || !snapshot.ById.ContainsKey(key))
            return;

        lastSeen[key] = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset? LastSeenOf(string? id)
        => Normalize(id) is { Length: > 0 } key && lastSeen.TryGetValue(key, out var value) ? value : null;

    /// <summary>待用入网码列表（不含明文码，明文只在签发时返回一次）。</summary>
    public IReadOnlyList<EnrollTicket> PendingTickets()
    {
        lock (gate)
        {
            TrimTickets();
            return tickets.Values.OrderByDescending(x => x.CreatedAt).ToArray();
        }
    }

    private bool SetEnabled(string id, bool enabled)
    {
        lock (gate)
        {
            var key = Normalize(id);
            if (!snapshot.ById.TryGetValue(key, out var record) || record.Enabled == enabled)
                return false;

            record.Enabled = enabled;
            Volatile.Write(ref snapshot, PeerSnapshot.Create(snapshot.Peers.ToList()));
            Save();
            logger.LogInformation(enabled ? "设备 {Id} 已恢复" : "设备 {Id} 已吊销", record.Id);
            return true;
        }
    }

    private void TrimTickets()
    {
        foreach (var dead in tickets.Values
                     .Where(x => x.Expires <= DateTimeOffset.UtcNow || x.UsedBy != null)
                     .ToArray())
            tickets.Remove(dead.Id.ToString("N"));
    }

    private string AllocateId(string requested)
    {
        var candidate = requested.Length == 0 ? "peer-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant() : requested;
        if (candidate.Length > 56)
            candidate = candidate[..56];

        for (var suffix = 2; !PeerProtocol.IsName(candidate) || snapshot.ById.ContainsKey(candidate); suffix++)
            candidate = (requested.Length == 0 ? "peer" : requested[..Math.Min(requested.Length, 56)]) + "-" + suffix;

        return candidate;
    }

    /// <summary>主机名 → 主体编号：小写，非法字符变连字符，压缩重复分隔符。</summary>
    private static string Normalize(string? name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length == 0)
            return "";

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
                builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        var normalized = builder.ToString().Trim('-');
        return normalized.Length > 64 ? normalized[..64].Trim('-') : normalized;
    }

    /// <summary>
    /// 合并外部改动：服务端 CLI 或管理员直接改了台账文件时，把内存快照换成文件内容。
    /// 只在签发 / 兑换 / 查询这些低频入口调用，认证热路径不碰磁盘。
    /// </summary>
    public void RefreshFromDisk()
    {
        if (stateFile == null)
            return;

        DateTime? stamp;
        try
        {
            stamp = File.Exists(stateFile) ? File.GetLastWriteTimeUtc(stateFile) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "入网台账状态检查失败，沿用内存台账：{File}", stateFile);
            return;
        }

        lock (gate)
        {
            if (Nullable.Equals(stamp, lastWriteUtc))
                return;

            tickets.Clear();
            lastSeen.Clear();
            Volatile.Write(ref snapshot, PeerSnapshot.Empty);
            Load();
        }
    }

    private void Load()
    {
        if (stateFile == null)
            return;

        try
        {
            lastWriteUtc = File.Exists(stateFile) ? File.GetLastWriteTimeUtc(stateFile) : null;
            if (!File.Exists(stateFile))
                return;

            var file = JsonSerializer.Deserialize<PeerRegistryFile>(File.ReadAllText(stateFile), JsonOptions);
            if (file == null)
                return;

            var peers = new List<PeerRecord>();
            foreach (var record in file.Peers ?? [])
            {
                record.Id = Normalize(record.Id);
                if (record.Id.Length == 0 || record.TokenFingerprint.Length == 0 || peers.Any(x => x.Id == record.Id))
                    continue;

                record.Name = string.IsNullOrWhiteSpace(record.Name) ? record.Id : record.Name;
                if (record.LastSeen > record.EnrolledAt)
                    lastSeen[record.Id] = record.LastSeen;
                peers.Add(record);
            }

            Volatile.Write(ref snapshot, PeerSnapshot.Create(peers));
            foreach (var ticket in file.Tickets ?? [])
            {
                if (ticket.Id != Guid.Empty && ticket.UsedBy == null && ticket.Expires > DateTimeOffset.UtcNow
                    && ticket.CodeFingerprint.Length > 0)
                    tickets[ticket.Id.ToString("N")] = ticket;
            }

            logger.LogInformation("已恢复 {Peers} 台入网设备、{Tickets} 张待用入网码：{File}",
                snapshot.ById.Count, tickets.Count, stateFile);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "入网台账读取失败，本次启动以空台账开始：{File}", stateFile);
        }
    }

    private void Save()
    {
        if (stateFile == null)
            return;

        try
        {
            var directory = Path.GetDirectoryName(stateFile);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var now = DateTimeOffset.UtcNow;
            foreach (var pair in lastSeen)
                if (snapshot.ById.TryGetValue(pair.Key, out var record) && pair.Value > record.LastSeen)
                    record.LastSeen = pair.Value;

            var payload = new PeerRegistryFile
            {
                Peers = snapshot.Peers.ToList(),
                Tickets = tickets.Values.Where(x => x.Expires > now).ToList(),
            };
            var temp = stateFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
            File.Move(temp, stateFile, true);
            lastWriteUtc = File.GetLastWriteTimeUtc(stateFile);
        }
        catch (Exception ex)
        {
            // 写文件失败不能影响内存台账，只告警
            logger.LogWarning(ex, "入网台账持久化失败（内存台账仍生效）：{File}", stateFile);
        }
    }
}

/// <summary>签发结果：明文码只在这里返回一次。</summary>
public sealed record IssuedTicket(Guid Id, string Code, DateTimeOffset Expires);

/// <summary>兑换成功后返回给客户端的凭据，明文 token 的唯一副本。</summary>
public sealed record PeerCredential(string Id, string Name, string Token);

/// <summary>入网设备记录。台账只保存 token 指纹，不保存明文。</summary>
public sealed class PeerRecord
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string TokenFingerprint { get; set; } = "";

    public DateTimeOffset EnrolledAt { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>待用入网码。只存码指纹，明文码不落台账也不落文件。</summary>
public sealed class EnrollTicket
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string CodeFingerprint { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset Expires { get; set; }

    public string? UsedBy { get; set; }
}

/// <summary>peers.json 的顶层结构。</summary>
public sealed class PeerRegistryFile
{
    public List<PeerRecord> Peers { get; set; } = [];

    public List<EnrollTicket> Tickets { get; set; } = [];
}

/// <summary>
/// 不可变的设备凭据快照。认证热路径只做一次指纹计算 + 字典查找；
/// 吊销与删除都会换成新快照，所以进行中的复检立刻看到变化。
/// </summary>
public sealed class PeerSnapshot
{
    internal static readonly PeerSnapshot Empty = Create([]);

    private PeerSnapshot(List<PeerRecord> peers)
    {
        Peers = peers.OrderByDescending(x => x.EnrolledAt).ToArray();
        ById = peers.ToDictionary(x => x.Id, x => x, StringComparer.OrdinalIgnoreCase);
        var byToken = new Dictionary<string, PeerRecord>(StringComparer.Ordinal);
        foreach (var peer in peers.Where(x => x.Enabled && x.TokenFingerprint.Length > 0))
            byToken[peer.TokenFingerprint] = peer;
        ByToken = byToken;
    }

    internal IReadOnlyList<PeerRecord> Peers { get; }

    internal IReadOnlyDictionary<string, PeerRecord> ById { get; }

    private IReadOnlyDictionary<string, PeerRecord> ByToken { get; }

    internal static PeerSnapshot Create(IReadOnlyList<PeerRecord> peers) => new(peers.ToList());

    /// <summary>按明文 token 命中一台有效设备，返回其主体编号。</summary>
    internal bool TryFind(string? token, out string id)
    {
        id = "";
        if (string.IsNullOrEmpty(token))
            return false;

        if (ByToken.TryGetValue(BanRegistry.Fingerprint(token), out var record) && record.Enabled)
        {
            id = record.Id;
            return true;
        }

        return false;
    }
}
