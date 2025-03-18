using System.Collections.Concurrent;

namespace IntercomEventing.Models;

internal class ObjectPool<T> where T : class
{
    private readonly ConcurrentBag<T> _objects = new();
    private readonly Func<T> _objectGenerator;

    public ObjectPool(Func<T> objectGenerator)
    {
        _objectGenerator = objectGenerator;
    }

    public T    Get()          => _objects.TryTake(out T? item) ? item : _objectGenerator();
    public void Return(T item) => _objects.Add(item);
}