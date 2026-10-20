using System;
using System.Collections.Generic;

namespace GDShrapt.Reader
{
    internal sealed class GDAstNodeIndexCollector : GDVisitor
    {
        private readonly HashSet<Type> _filter;

        public List<GDNode> AllNodes { get; }
        public Dictionary<Type, List<GDNode>> NodesByType { get; } = new Dictionary<Type, List<GDNode>>();

        /// <param name="expectedNodes">Capacity hint for <see cref="AllNodes"/> (a frozen root knows its node count).</param>
        public GDAstNodeIndexCollector(Type[] filter, int expectedNodes = 0)
        {
            _filter = filter != null ? new HashSet<Type>(filter) : null;
            AllNodes = new List<GDNode>(expectedNodes);
        }

        public override void WillVisit(GDNode node)
        {
            AllNodes.Add(node);

            var type = node.GetType();

            if (_filter == null || _filter.Contains(type))
            {
                if (!NodesByType.TryGetValue(type, out var list))
                {
                    list = new List<GDNode>();
                    NodesByType[type] = list;
                }

                list.Add(node);
            }
        }
    }
}
