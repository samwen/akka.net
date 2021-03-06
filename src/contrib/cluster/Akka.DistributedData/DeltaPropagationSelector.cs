﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Akka.Actor;
using Akka.DistributedData.Internal;

namespace Akka.DistributedData
{
    internal abstract class DeltaPropagationSelector
    {
        private ImmutableDictionary<string, long> _deltaCounter = ImmutableDictionary<string, long>.Empty;
        private ImmutableDictionary<string, ImmutableSortedDictionary<long, IReplicatedData>> _deltaEntries = ImmutableDictionary<string, ImmutableSortedDictionary<long, IReplicatedData>>.Empty;
        private ImmutableDictionary<string, ImmutableDictionary<Address, long>> _deltaSentToNode = ImmutableDictionary<string, ImmutableDictionary<Address, long>>.Empty;
        private long _deltaNodeRoundRobinCounter = 0L;

        public long PropagationCount { get; private set; }

        public abstract int GossipInternalDivisor { get; }
        protected abstract ImmutableArray<Address> AllNodes { get; }
        protected abstract int MaxDeltaSize { get; }
        protected abstract DeltaPropagation CreateDeltaPropagation(ImmutableDictionary<string, Tuple<IReplicatedData, long, long>> deltas);

        public long CurrentVersion(string key) => _deltaCounter.GetValueOrDefault(key, 0L);

        public void Update(string key, IReplicatedData delta)
        {
            // bump the counter for each update
            var version = _deltaCounter.GetValueOrDefault(key, 0L) + 1;
            _deltaCounter = _deltaCounter.SetItem(key, version);

            var deltaEntriesForKey = _deltaEntries.GetValueOrDefault(key, ImmutableSortedDictionary<long, IReplicatedData>.Empty);
            _deltaEntries = _deltaEntries.SetItem(key, deltaEntriesForKey.SetItem(version, delta));
        }

        public void Delete(string key)
        {
            _deltaEntries = _deltaEntries.Remove(key);
            _deltaCounter = _deltaCounter.Remove(key);
            _deltaSentToNode = _deltaSentToNode.Remove(key);
        }

        // 2 - 10 nodes
        public virtual int NodeSliceSize(int allNodesSize) =>
            Math.Min(Math.Max((allNodesSize / GossipInternalDivisor) + 1, 2), Math.Min(allNodesSize, 10));

        public ImmutableDictionary<Address, DeltaPropagation> CollectPropagations()
        {
            PropagationCount++;
            var all = AllNodes;
            if (all.IsEmpty)
                return ImmutableDictionary<Address, DeltaPropagation>.Empty;
            else
            {
                // For each tick we pick a few nodes in round-robin fashion, 2 - 10 nodes for each tick.
                // Normally the delta is propagated to all nodes within the gossip tick, so that
                // full state gossip is not needed.
                var sliceSize = NodeSliceSize(all.Length);
                ImmutableArray<Address> slice;
                if (all.Length <= sliceSize) slice = all;
                else
                {
                    var start = (int)(_deltaNodeRoundRobinCounter % all.Length);
                    var buffer = new Address[sliceSize];
                    for (int i = 0; i < sliceSize; i++)
                    {
                        buffer[i] = all[(start + i) % all.Length];
                    }
                    slice = ImmutableArray.CreateRange(buffer);
                }

                _deltaNodeRoundRobinCounter += sliceSize;

                var result = ImmutableDictionary<Address, DeltaPropagation>.Empty.ToBuilder();
                var cache = new Dictionary<Tuple<string, long, long>, IReplicatedData>();
                foreach (var node in slice)
                {
                    // collect the deltas that have not already been sent to the node and merge
                    // them into a delta group
                    var deltas = ImmutableDictionary<string, Tuple<IReplicatedData, long, long>>.Empty.ToBuilder();
                    foreach (var entry in _deltaEntries)
                    {
                        var key = entry.Key;
                        var entries = entry.Value;

                        var deltaSentToNodeForKey = _deltaSentToNode.GetValueOrDefault(key, ImmutableDictionary<Address, long>.Empty);
                        var j = deltaSentToNodeForKey.GetValueOrDefault(node, 0L);
                        var deltaEntriesAfterJ = DeltaEntriesAfter(entries, j);
                        if (!deltaEntriesAfterJ.IsEmpty)
                        {
                            var fromSeqNr = deltaEntriesAfterJ.Keys.First(); // should be min
                            var toSeqNr = deltaEntriesAfterJ.Keys.Last(); // should be max

                            // in most cases the delta group merging will be the same for each node,
                            // so we cache the merged results
                            var cacheKey = Tuple.Create(key, fromSeqNr, toSeqNr);
                            IReplicatedData deltaGroup;
                            if (!cache.TryGetValue(cacheKey, out deltaGroup))
                            {
                                using (var e = deltaEntriesAfterJ.Values.GetEnumerator())
                                {
                                    e.MoveNext();
                                    deltaGroup = e.Current;
                                    while (e.MoveNext())
                                    {
                                        deltaGroup = deltaGroup.Merge(e.Current);
                                        if (deltaGroup is IReplicatedDeltaSize s && s.DeltaSize > MaxDeltaSize)
                                        {
                                            deltaGroup = DeltaPropagation.NoDeltaPlaceholder;
                                        }
                                    }
                                }

                                cache[cacheKey] = deltaGroup;
                            }

                            deltas[key] = Tuple.Create(deltaGroup, fromSeqNr, toSeqNr);
                            _deltaSentToNode = _deltaSentToNode.SetItem(key, deltaSentToNodeForKey.SetItem(node, toSeqNr));
                        }
                    }

                    if (deltas.Count > 0)
                    {
                        // Important to include the pruning state in the deltas. For example if the delta is based
                        // on an entry that has been pruned but that has not yet been performed on the target node.
                        var deltaPropagation = CreateDeltaPropagation(deltas.ToImmutable());
                        result[node] = deltaPropagation;
                    }
                }

                return result.ToImmutable();
            }
        }

        public bool HasDeltaEntries(string key)
        {
            ImmutableSortedDictionary<long, IReplicatedData> entries;
            if (_deltaEntries.TryGetValue(key, out entries))
            {
                return !entries.IsEmpty;
            }

            return false;
        }

        public void CleanupDeltaEntries()
        {
            var all = AllNodes;
            if (all.IsEmpty)
                _deltaEntries = ImmutableDictionary<string, ImmutableSortedDictionary<long, IReplicatedData>>.Empty;
            else
            {
                _deltaEntries = _deltaEntries.Select(entry =>
                    {
                        var minVersion = FindSmallestVersionPropagatedToAllNodes(entry.Key, all);
                        var deltasAfterMin = DeltaEntriesAfter(entry.Value, minVersion);
                        //TODO perhaps also remove oldest when deltaCounter is too far ahead (e.g. 10 cycles)

                        return new KeyValuePair<string, ImmutableSortedDictionary<long, IReplicatedData>>(entry.Key, deltasAfterMin);
                    })
                    .ToImmutableDictionary();
            }
        }

        public void CleanupRemovedNode(Address address)
        {
            _deltaSentToNode = _deltaSentToNode
                .Select(entry => new KeyValuePair<string, ImmutableDictionary<Address, long>>(entry.Key, entry.Value.Remove(address)))
                .ToImmutableDictionary();
        }

        private ImmutableSortedDictionary<long, IReplicatedData> DeltaEntriesAfter(
            ImmutableSortedDictionary<long, IReplicatedData> entries, long version) =>
            entries.Where(e => e.Key > version).ToImmutableSortedDictionary();

        private long FindSmallestVersionPropagatedToAllNodes(string key, IEnumerable<Address> nodes)
        {
            ImmutableDictionary<Address, long> deltaSentToNodeForKey;
            if (_deltaSentToNode.TryGetValue(key, out deltaSentToNodeForKey) && !deltaSentToNodeForKey.IsEmpty)
            {
                return nodes.Any(node => !deltaSentToNodeForKey.ContainsKey(node))
                    ? 0L
                    : deltaSentToNodeForKey.Values.Min();
            }

            return 0L;
        }
    }
}