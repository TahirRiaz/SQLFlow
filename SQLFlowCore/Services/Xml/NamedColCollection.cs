using System;
using System.Collections;
using System.Collections.Generic;

namespace SQLFlowCore.Services.Xml
{
    /// <summary>
    /// Represents a collection of named columns.
    /// </summary>
    internal abstract class NamedColCollection : ICollection, IEnumerable
    {
        /// <summary>
        /// A list of column objects.
        /// </summary>
        private List<object> _columns = new();
        /// <summary>
        /// A dictionary that maps column names to their indices in the _columns list.
        /// </summary>
        private Dictionary<string, int> _indexByName = new();
        /// <summary>
        /// Indicates whether the column names are case sensitive.
        /// </summary>
        private bool _caseSensitive = true;
        /// <summary>
        /// Initializes a new instance of the NamedColCollection class.
        /// </summary>
        protected NamedColCollection()
        {
        }
        /// <summary>
        /// Adds a column to the collection.
        /// </summary>
        /// <param name="column">The column to add.</param>
        protected void Add(object column)
        {
            Add(column, null);
        }
        /// <summary>
        /// Adds a column with a specified name to the collection.
        /// </summary>
        /// <param name="column">The column to add.</param>
        /// <param name="columnName">The name of the column.</param>
        protected void Add(object column, string columnName)
        {
            if (columnName != null)
            {
                _indexByName[columnName] = _columns.Count;
            }
            _columns.Add(column);
        }
        /// <summary>
        /// Gets the index of a column by its name.
        /// </summary>
        /// <param name="columnName">The name of the column.</param>
        /// <returns>The index of the column, or -1 if the column is not found.</returns>
        public int GetIndex(string columnName)
        {
            return !_indexByName.ContainsKey(columnName) ? -1 : _indexByName[columnName];
        }
        /// <summary>
        /// Copies the elements of the collection to an Array, starting at a particular Array index.
        /// </summary>
        /// <param name="array">The one-dimensional Array that is the destination of the elements copied from ICollection.</param>
        /// <param name="index">The zero-based index in array at which copying begins.</param>
        void ICollection.CopyTo(Array array, int index)
        {
            _columns.ToArray().CopyTo(array, index);
        }
        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>An enumerator that can be used to iterate through the collection.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return _columns.GetEnumerator();
        }
        /// <summary>
        /// Gets or sets a value indicating whether the column names are case sensitive.
        /// </summary>
        public bool CaseSensitive
        {
            get => _caseSensitive;
            set
            {
                if (_caseSensitive != value)
                {
                    _indexByName = !value ? new Dictionary<string, int>(_indexByName, StringComparer.InvariantCultureIgnoreCase) : new Dictionary<string, int>(_indexByName);
                    _caseSensitive = value;
                }
            }
        }
        /// <summary>
        /// Gets the names of the columns in the collection.
        /// </summary>
        public string[] Names
        {
            get
            {
                string[] array = new string[_indexByName.Count];
                _indexByName.Keys.CopyTo(array, 0);
                return array;
            }
        }
        /// <summary>
        /// Gets the number of elements contained in the collection.
        /// </summary>
        public int Count => _columns.Count;
        /// <summary>
        /// Gets a value indicating whether access to the ICollection is synchronized (thread safe).
        /// </summary>
        bool ICollection.IsSynchronized => ((ICollection)_columns).IsSynchronized;
        /// <summary>
        /// Gets an object that can be used to synchronize access to the ICollection.
        /// </summary>
        object ICollection.SyncRoot => ((ICollection)_columns).SyncRoot;
        /// <summary>
        /// Gets or sets the column at the specified index.
        /// </summary>
        /// <param name="columnIndex">The zero-based index of the column to get or set.</param>
        /// <returns>The column at the specified index.</returns>
        protected object this[int columnIndex]
        {
            get => columnIndex <= -1 || columnIndex >= _columns.Count ? null : _columns[columnIndex];
            set
            {
                if (columnIndex > -1 && columnIndex < _columns.Count)
                {
                    _columns[columnIndex] = value;
                }
                else
                {
                    string[] textArray1 = new string[] { "No column was found at index ", columnIndex.ToString("###,##0"), " in column collection of length ", _columns.Count.ToString("###,##0"), "." };
                    throw new IndexOutOfRangeException(string.Concat(textArray1));
                }
            }
        }
        /// <summary>
        /// Gets or sets the column with the specified name.
        /// </summary>
        /// <param name="columnName">The name of the column to get or set.</param>
        /// <returns>The column with the specified name.</returns>
        protected object this[string columnName]
        {
            get => this[GetIndex(columnName)];
            set
            {
                int index = GetIndex(columnName);
                if (index > -1)
                {
                    this[index] = value;
                }
                else
                {
                    Add(value, columnName);
                }
            }
        }
    }

}

