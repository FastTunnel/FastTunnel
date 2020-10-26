using FastTunnel.Core.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FastTunnel.Core.Global
{
    public static class FastTunnelGlobal
    {
        static IList<IFastTunntlfilter> m_filters = new List<IFastTunntlfilter>();

        static Dictionary<Type, object> m_customHandlers = new Dictionary<Type, object>();

        public static void AddFilter(IFastTunntlfilter filter)
        {
            m_filters.Add(filter);
        }

        public static IEnumerable<IFastTunntlfilter> GetFilters(Type type)
        {
            return m_filters.Where(x => { return x.GetType().GetInterfaces().Contains(type); });
        }

        public static void AddCustomHandler<Tbase, Impl>(Impl _impl)
            where Impl : class, Tbase
        {
            m_customHandlers.Add(typeof(Tbase), _impl);
        }

        public static Tbase GetCustomHandler<Tbase>()
            where Tbase : class
        {
            object custom;
            m_customHandlers.TryGetValue(typeof(Tbase), out custom);
            return (Tbase)custom;
        }
    }
}
