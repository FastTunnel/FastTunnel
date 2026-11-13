// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

namespace FastTunnel.Core.Protocol;

public class ProtocolConst
{
    public const string FASTTUNNEL_VERSION = "FT_VERSION";
    public const string FASTTUNNEL_MSGID = "FT_MSGID";
    public const string FASTTUNNEL_TOKEN = "FT_TOKEN";
    public const string HTTP_METHOD_SWAP = "PROXY";

    /// <summary>
    /// 多路复用会话的握手方法：<c>MUX /{sessionId} HTTP/1.1</c>。
    /// 握手之后该连接上的字节流全部是 <see cref="Mux.MuxFrame"/> 帧，不再有 HTTP 语义。
    /// </summary>
    public const string HTTP_METHOD_MUX = "MUX";
}
