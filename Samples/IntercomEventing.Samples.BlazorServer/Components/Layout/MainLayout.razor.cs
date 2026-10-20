using IntercomEventing.Samples.BlazorServer.Shared;
using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Samples.BlazorServer.Components.Layout;

public partial class MainLayout
{
    [Inject]
    private CircuitVisitor Visitor { get; set; } = default!;
}
