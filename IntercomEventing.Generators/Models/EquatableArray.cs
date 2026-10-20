using System.Collections;

namespace IntercomEventing.Generators.Models;

/// <summary>
/// An immutable array compared by its elements, so pipeline models that hold collections stay equal across generator runs
/// and their downstream steps stay cached. <see cref="System.Collections.Immutable.ImmutableArray{T}"/> compares by reference.
/// </summary>
/// <typeparam name="T">The element type; compared with its own <see cref="IEquatable{T}"/> implementation.</typeparam>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    //null for default(EquatableArray<T>), which behaves as an empty array
    private readonly T[]? _items;

    /// <summary>
    /// An array without elements.
    /// </summary>
    public static EquatableArray<T> Empty => new([]);

    /// <summary>
    /// Number of elements.
    /// </summary>
    public int Count => _items?.Length ?? 0;

    /// <summary>
    /// The element at <paramref name="index"/>.
    /// </summary>
    public T this[int index] => (_items ?? [])[index];

    /// <summary>
    /// Wraps <paramref name="items"/>; the caller must not change the array afterwards.
    /// </summary>
    public EquatableArray(T[] items)
    {
        _items = items;
    }

    /// <inheritdoc />
    public bool Equals(EquatableArray<T> other)
    {
        var items = _items ?? [];
        var otherItems = other._items ?? [];
        if (items.Length != otherItems.Length)
        {
            return false;
        }
        for (var index = 0; index < items.Length; index++)
        {
            if (!items[index].Equals(otherItems[index]))
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is EquatableArray<T> other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        //netstandard2.0 has no System.HashCode; a simple multiplicative combination is enough for cache lookups
        unchecked
        {
            var hash = 17;
            foreach (var item in _items ?? [])
            {
                hash = (hash * 31) + item.GetHashCode();
            }
            return hash;
        }
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
    {
        IEnumerable<T> items = _items ?? [];
        return items.GetEnumerator();
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
