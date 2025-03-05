using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLFlowCore.Lineage
{
    /// <summary>
    /// Represents a simple priority queue used for Dijkstra's algorithm.
    /// </summary>
    /// <typeparam name="T">The type of items in the priority queue.</typeparam>
    /// <typeparam name="TPriority">The type of the priority associated with the items. This type must implement <see cref="IComparable{TPriority}"/>.</typeparam>
    /// <remarks>
    /// This priority queue is implemented as a sorted list. Each item in the queue has an associated priority. 
    /// When an item is enqueued, it is added to the list in a position according to its priority. 
    /// The item with the highest priority (i.e., the smallest value, as defined by the <see cref="IComparable{TPriority}.CompareTo"/> method) is always at the front of the queue.
    /// </remarks>
    internal class PriorityQueue<T, TPriority> where TPriority : IComparable<TPriority>
    {
        private List<(T, TPriority)> data;

        internal PriorityQueue()
        {
            data = new List<(T, TPriority)>();
        }

        internal void Enqueue(T item, TPriority priority)
        {
            data.Add((item, priority));
            data.Sort((x, y) => x.Item2.CompareTo(y.Item2));
        }

        internal T Dequeue()
        {
            var item = data[0].Item1;
            data.RemoveAt(0);
            return item;
        }

        internal bool IsEmpty => !data.Any();
    }
}
