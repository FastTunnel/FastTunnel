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

        public static void AddFilter(IFastTunntlfilter filter)
        {
            m_filters.Add(filter);
        }

        public static IEnumerable<IFastTunntlfilter> GetFilters(Type type)
        {
            return m_filters.Where(x => { return x.GetType().GetInterfaces().Contains(type); });
        }
    }
}
