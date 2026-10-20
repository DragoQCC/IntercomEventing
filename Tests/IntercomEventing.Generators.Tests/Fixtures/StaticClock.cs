namespace IntercomEventing.Generators.Tests.Fixtures
{
    /// <summary>
    /// A static producer in a block-scoped namespace: the generator gives it static members.
    /// </summary>
    public static partial class StaticClock
    {
        [IntercomEvent]
        private static readonly IntercomEvent s_ticked = new();

        internal static object TickedBackingEvent => s_ticked;

        public static Task TickAsync()
        {
            return s_ticked.RaiseAsync();
        }
    }
}
