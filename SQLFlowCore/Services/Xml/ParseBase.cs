using System;
using System.Collections;
using System.Runtime.InteropServices;

namespace SQLFlowCore.Services.Xml
{
    /// <summary>
    /// Represents the base class for parsing operations.
    /// </summary>
    internal abstract class ParseBase : IDisposable, IEnumerator, IEnumerable
    {
        protected int ColumnsCount;
        protected string[] values = new string[10];
        protected ulong currentRecord;
        protected bool Disposed;

        protected ParseBase()
        {
        }

        protected void CheckDisposed()
        {
            if (Disposed)
            {
                throw new ObjectDisposedException(GetType().FullName, "This object has been previously disposed. Methods on this object can no longer be called.");
            }
        }

        public void Close()
        {
            ((IDisposable)this).Dispose();
        }

        protected abstract void Dispose(bool disposing);
        public abstract bool ReadRecord();
        IEnumerator IEnumerable.GetEnumerator()
        {
            return this;
        }

        bool IEnumerator.MoveNext()
        {
            return ReadRecord();
        }

        void IEnumerator.Reset()
        {
            throw new NotSupportedException("Reset is not currently supported by the IEnumerable implementation supplied by " + GetType().FullName + ".");
        }

        void IDisposable.Dispose()
        {
            if (!Disposed)
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Gets the value at the specified column index.
        /// </summary>
        /// <param name="columnIndex">The index of the column.</param>
        /// <returns>The value at the specified column index.</returns>
        public string this[int columnIndex]
        {
            get
            {
                CheckDisposed();
                string str = columnIndex <= -1 || columnIndex >= ColumnsCount ? "" : values[columnIndex];
                return currentRecord <= 10 || columnIndex % 2 != 1 ? str : "DEMO";
            }
        }

        /// <summary>
        /// Gets the values.
        /// </summary>
        public string[] Values
        {
            get
            {
                CheckDisposed();
                string[] strArray = new string[ColumnsCount];
                for (int i = 0; i < ColumnsCount; i++)
                {
                    strArray[i] = this[i];
                }
                return strArray;
            }
        }

        /// <summary>
        /// Gets the current record.
        /// </summary>
        public ulong CurrentRecord => currentRecord - 1L;

        object IEnumerator.Current => Values;

        [StructLayout(LayoutKind.Sequential, Size = 1)]
        private struct StaticSettings
        {
            public const int InitialColumnCount = 10;
            public const string Demo = "DEMO";
        }
    }
}

