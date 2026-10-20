namespace IntercomEventing.Generators.Tests.Fixtures;

/// <summary>
/// A generic producer whose payload is its own type parameter.
/// </summary>
public partial class Box<T>
{
    [IntercomEvent]
    private readonly IntercomEvent<T> _changed = new();

    public Task SetAsync(T value)
    {
        return _changed.RaiseAsync(value, caller: this);
    }
}

/// <summary>
/// Generic types nested in a generic type; the generated file redeclares both levels.
/// </summary>
public partial class Outer<TKey>
{
    public partial class Inner<TValue>
    {
        [IntercomEvent]
        private readonly StateChangeEvent<TValue> m_stateChanged;

        public Inner(TValue initialState)
        {
            m_stateChanged = new StateChangeEvent<TValue>(initialState);
        }

        public Task<bool> ChangeAsync(TValue newState)
        {
            return m_stateChanged.ChangeStateAsync(newState, caller: this);
        }
    }
}
