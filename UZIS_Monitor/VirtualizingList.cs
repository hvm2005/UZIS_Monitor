using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace UZIS_Monitor
{
    public class VirtualizingList<T> : IList, IList<T>
    {
        private readonly IList<T> _source;

        public VirtualizingList(IList<T> source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        // ГЛАВНОЕ: DataGrid будет вызывать этот геттер только для видимых строк
        public T this[int index]
        {
            get => _source[index];
            set => throw new NotSupportedException();
        }

        object? IList.this[int index]
        {
            get => _source[index];
            set => throw new NotSupportedException();
        }

        public int Count => _source.Count;

        // Остальные методы реализуем по минимуму, так как DataGrid для отображения их не использует
        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;

        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(object? value) => _source.Contains((T)value!);
        public int IndexOf(object? value) => _source.IndexOf((T)value!);
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public void CopyTo(Array array, int index) => throw new NotSupportedException();

        public IEnumerator<T> GetEnumerator() => _source.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _source.GetEnumerator();

        // Реализация IList<T>
        int IList<T>.IndexOf(T item) => _source.IndexOf(item);
        void IList<T>.Insert(int index, T item) => throw new NotSupportedException();
        void IList<T>.RemoveAt(int index) => throw new NotSupportedException();
        void ICollection<T>.Add(T item) => throw new NotSupportedException();
        void ICollection<T>.Clear() => throw new NotSupportedException();
        bool ICollection<T>.Contains(T item) => _source.Contains(item);
        void ICollection<T>.CopyTo(T[] array, int arrayIndex) => _source.CopyTo(array, arrayIndex);
        bool ICollection<T>.Remove(T item) => throw new NotSupportedException();
    }
}
