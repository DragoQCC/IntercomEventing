using IntercomEventing.Blazor.Tests.TestSupport;
using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests;

/// <summary>
/// <see cref="ComponentEventSubscriptions"/> on its own, with <see cref="FakeComponent"/> standing in for the component's
/// protected members, for the edge cases a real renderer cannot stage deterministically.
/// </summary>
public class ComponentEventSubscriptionsTests
{
    [Test]
    public async Task SynchronousHandler_RunsThroughInvokeAsync_ThenRendersOnce()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var receivedPayloads = new List<int>();

        events.Subscribe(source, call => receivedPayloads.Add(call.Payload));
        await source.RaiseAsync(5);

        await Assert.That(string.Join(",", receivedPayloads)).IsEqualTo("5");
        await Assert.That(component.InvokeCount).IsEqualTo(1);
        await Assert.That(component.RenderCount).IsEqualTo(1);
    }

    [Test]
    public async Task RenderAfterHandlerFalse_SkipsTheAutomaticRender()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var handlerCalls = 0;

        events.Subscribe(source, call => handlerCalls++, renderAfterHandler: false);
        await source.RaiseAsync(1);

        await Assert.That(handlerCalls).IsEqualTo(1);
        await Assert.That(component.RenderCount).IsEqualTo(0);
    }

    [Test]
    public async Task CallQueuedForTheRenderer_IsSkipped_WhenTheComponentIsDisposedBeforeItRuns()
    {
        var component = new FakeComponent
        {
            HoldWorkItems = true,
        };
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var handlerCalls = 0;
        events.Subscribe(source, call => handlerCalls++);

        //the raise waits for the queued work item, like a raise whose handler waits for a busy renderer
        var raise = source.RaiseAsync(1);
        var queuedWorkItems = component.HeldWorkItemCount;
        await events.DisposeAsync();
        await component.RunHeldWorkItemsAsync();
        await raise;

        await Assert.That(queuedWorkItems).IsEqualTo(1);
        await Assert.That(handlerCalls).IsEqualTo(0);
        await Assert.That(component.RenderCount).IsEqualTo(0);
        await Assert.That(source.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task SubscribingAfterDisposal_SubscribesNothingAndReturnsNull()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var hookRuns = 0;
        await events.DisposeAsync();

        var subscription = events.Subscribe(source, call => { });
        var subscriptionWithHooks = await events.SubscribeAsync(source, call => { }, new()
        {
            OnSubscribed = _ =>
            {
                hookRuns++;
                return Task.CompletedTask;
            },
        });
        var resubscription = events.Resubscribe(null, source, call => { });

        await Assert.That(events.IsDisposed).IsTrue();
        await Assert.That(subscription).IsNull();
        await Assert.That(subscriptionWithHooks).IsNull();
        await Assert.That(resubscription).IsNull();
        await Assert.That(hookRuns).IsEqualTo(0);
        await Assert.That(source.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task StaticRendering_SubscribesNothingAndReturnsNull()
    {
        var component = new FakeComponent
        {
            RendererInfo = new RendererInfo("Static", isInteractive: false),
        };
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var hookRuns = 0;

        var subscription = events.Subscribe(source, call => { });
        var subscriptionWithHooks = await events.SubscribeAsync(source, call => { }, new()
        {
            OnSubscribed = _ =>
            {
                hookRuns++;
                return Task.CompletedTask;
            },
        });

        await Assert.That(subscription).IsNull();
        await Assert.That(subscriptionWithHooks).IsNull();
        await Assert.That(hookRuns).IsEqualTo(0);
        await Assert.That(source.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task WithoutGetRendererInfo_SubscribesEvenWhileRenderingStatically()
    {
        var component = new FakeComponent
        {
            RendererInfo = new RendererInfo("Static", isInteractive: false),
        };
        var events = component.CreateSubscriptions(guardPrerendering: false);
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);

        var subscription = events.Subscribe(source, call => { });

        await Assert.That(subscription).IsNotNull();
        await Assert.That(source.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task RendererThatCannotReportItsInfo_CountsAsInteractive()
    {
        var component = new FakeComponent
        {
            RendererInfo = null,
        };
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);

        var subscription = events.Subscribe(source, call => { });

        await Assert.That(subscription).IsNotNull();
        await Assert.That(source.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_RemovesEverySubscription_AndCallingItAgainDoesNothing()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var numbers = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var words = new IntercomEvent<string>(TestEventOptions.WaitForEveryHandler);
        var numberSubscription = events.Subscribe(numbers, call => { });
        var wordSubscription = await events.SubscribeAsync(words, call => { });
        var removedEarly = events.Subscribe(numbers, call => { });
        removedEarly!.Dispose();

        await events.DisposeAsync();
        await events.DisposeAsync();

        await Assert.That(numberSubscription!.IsActive).IsFalse();
        await Assert.That(wordSubscription!.IsActive).IsFalse();
        await Assert.That(numbers.SubscriberCount).IsEqualTo(0);
        await Assert.That(words.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel(DefaultExceptionHandlerScope.NotInParallelKey)]
    public async Task DisposeAsync_ReportsAFailingUnsubscribeHookToTheEventsOwnHandler_AndStillRemovesTheOtherSubscriptions()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var eventFailures = new EventFailureRecorder();
        var source = new IntercomEvent<int>(eventFailures.EventOptions)
        {
            Name = "UnsubscribeHookFailsDuringHelperDisposal",
        };
        using var processDefaultHandler = new DefaultExceptionHandlerScope(source.Name);
        var secondHookRan = false;
        var failingSubscription = await events.SubscribeAsync(source, call => { }, new()
        {
            OnUnsubscribed = _ => throw new InvalidOperationException("first hook failed"),
        });
        await events.SubscribeAsync(source, call => { }, new()
        {
            OnUnsubscribed = _ =>
            {
                secondHookRan = true;
                return Task.CompletedTask;
            },
        });

        await events.DisposeAsync();
        var failure = await eventFailures.WaitForFirstFailureAsync();

        await Assert.That(secondHookRan).IsTrue();
        await Assert.That(source.SubscriberCount).IsEqualTo(0);
        await Assert.That(eventFailures.Failures.Count).IsEqualTo(1);
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.OnUnsubscribed);
        await Assert.That(failure.EventName).IsEqualTo(source.Name);
        await Assert.That(failure.SubscriptionId).IsEqualTo(failingSubscription!.Id);
        await Assert.That(failure.Exception.Message).IsEqualTo("first hook failed");
        //the event has options of its own, so EventingOptions.Default is not involved
        await Assert.That(processDefaultHandler.Failures.Count).IsEqualTo(0);
    }

    [Test]
    [NotInParallel(DefaultExceptionHandlerScope.NotInParallelKey)]
    public async Task DisposeAsync_ReportsAFailingUnsubscribeHookOfAnEventWithoutOwnOptions_ToTheDefaultInEffectAtThatMoment()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>
        {
            Name = "UnsubscribeHookOfDefaultOptionsEventFails",
        };
        var failingSubscription = await events.SubscribeAsync(source, call => { }, new()
        {
            OnUnsubscribed = _ => throw new InvalidOperationException("hook failed"),
        });
        //replaced after subscribing: the event's options are read when the hook fails, not when it was subscribed
        using var processDefaultHandler = new DefaultExceptionHandlerScope(source.Name);

        await events.DisposeAsync();
        var failure = await processDefaultHandler.WaitForFirstFailureAsync();

        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.OnUnsubscribed);
        await Assert.That(failure.SubscriptionId).IsEqualTo(failingSubscription!.Id);
        await Assert.That(failure.Exception.Message).IsEqualTo("hook failed");
    }

    [Test]
    public async Task DisposeAsync_DoesNotThrow_WhenTheEventsExceptionHandlerItselfThrows()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var eventFailures = new EventFailureRecorder(throwAfterRecording: true);
        var source = new IntercomEvent<int>(eventFailures.EventOptions);
        await events.SubscribeAsync(source, call => { }, new()
        {
            OnUnsubscribed = _ => throw new InvalidOperationException("unsubscribe hook failed"),
        });

        Exception? disposalException = null;
        try
        {
            await events.DisposeAsync();
        }
        catch (Exception exception)
        {
            disposalException = exception;
        }

        await Assert.That(disposalException).IsNull();
        //the hook failure reached the event's exception handler, which then threw
        await Assert.That(eventFailures.Failures.Count).IsEqualTo(1);
        await Assert.That(source.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task Dispose_RemovesEverySubscriptionAtOnce_WithoutWaitingForUnsubscribeHooks()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var hookRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookStarted = false;
        await events.SubscribeAsync(source, call => { }, new()
        {
            OnUnsubscribed = _ =>
            {
                hookStarted = true;
                return hookRelease.Task;
            },
        });

        events.Dispose();
        var subscribersAfterDispose = source.SubscriberCount;
        hookRelease.SetResult();
        //event disposal waits for unsubscribe hooks started in the background
        await source.DisposeAsync();

        await Assert.That(events.IsDisposed).IsTrue();
        await Assert.That(subscribersAfterDispose).IsEqualTo(0);
        await Assert.That(hookStarted).IsTrue();
    }

    [Test]
    public async Task SubscribeAsync_WhenTheComponentIsDisposedWhileOnSubscribedRuns_UndoesTheSubscriptionAndReturnsNull()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var subscribeHookRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribeHookRuns = 0;

        var subscribing = events.SubscribeAsync(source, call => { }, new()
        {
            OnSubscribed = _ => subscribeHookRelease.Task,
            OnUnsubscribed = _ =>
            {
                unsubscribeHookRuns++;
                return Task.CompletedTask;
            },
        });
        await events.DisposeAsync();
        subscribeHookRelease.SetResult();
        var subscription = await subscribing;

        await Assert.That(subscription).IsNull();
        await Assert.That(unsubscribeHookRuns).IsEqualTo(1);
        await Assert.That(source.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task Subscribe_WhenTheComponentIsDisposedWhileTheEventSubscribes_RemovesTheNewSubscriptionAndReturnsNull()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var realEvent = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        //stands in for a disposal on another thread that lands after the disposed check but before the subscription is tracked
        var source = new SubscribeInterceptingEvent<EventCall<int>>(realEvent, beforeSubscribe: events.Dispose);

        var subscription = events.Subscribe(source, call => { });

        await Assert.That(events.IsDisposed).IsTrue();
        await Assert.That(subscription).IsNull();
        await Assert.That(realEvent.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task Resubscribe_KeepsTheSubscriptionWhileTheEventIsTheSame()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        IIntercomEvent<EventCall<int>> source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);

        var first = events.Resubscribe(null, source, call => { });
        var second = events.Resubscribe(first, source, call => { });

        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(second!.IsActive).IsTrue();
        await Assert.That(source.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task Resubscribe_MovesToANewEvent_AndToNullUnsubscribes()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var firstEvent = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var secondEvent = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var receivedPayloads = new List<int>();

        var onFirst = events.Resubscribe(null, firstEvent, call => receivedPayloads.Add(call.Payload));
        var onSecond = events.Resubscribe(onFirst, secondEvent, call => receivedPayloads.Add(call.Payload));
        await firstEvent.RaiseAsync(1);
        await secondEvent.RaiseAsync(2);
        var subscribersOfSecondBeforeClearing = secondEvent.SubscriberCount;
        var onNothing = events.Resubscribe(onSecond, null, call => receivedPayloads.Add(call.Payload));

        await Assert.That(onFirst!.IsActive).IsFalse();
        await Assert.That(firstEvent.SubscriberCount).IsEqualTo(0);
        await Assert.That(subscribersOfSecondBeforeClearing).IsEqualTo(1);
        await Assert.That(string.Join(",", receivedPayloads)).IsEqualTo("2");
        await Assert.That(onNothing).IsNull();
        await Assert.That(secondEvent.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandlerException_GoesToDispatchExceptionAsync_WhenTheSubscriptionHasNoHandlerOfItsOwn()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);

        events.Subscribe(source, ThrowForCall);
        await source.RaiseAsync(3);

        await Assert.That(component.DispatchedExceptions.Count).IsEqualTo(1);
        await Assert.That(component.DispatchedExceptions[0].Message).IsEqualTo("handler failed for 3");
        //a failed handler is not followed by a render, as after a failed Blazor UI event handler
        await Assert.That(component.RenderCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandlerException_GoesToTheSubscriptionsOwnHandlerOnTheRenderer_ThenRenders()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var handledMessages = new List<string>();

        events.Subscribe(source, ThrowForCall, onException: (exception, call) => handledMessages.Add(exception.Message));
        await source.RaiseAsync(4);

        await Assert.That(string.Join(",", handledMessages)).IsEqualTo("handler failed for 4");
        await Assert.That(component.DispatchedExceptions.Count).IsEqualTo(0);
        //one InvokeAsync for the handler and one for its exception handler, which is followed by a render
        await Assert.That(component.InvokeCount).IsEqualTo(2);
        await Assert.That(component.RenderCount).IsEqualTo(1);
    }

    [Test]
    [NotInParallel(DefaultExceptionHandlerScope.NotInParallelKey)]
    public async Task ThrowingOwnExceptionHandler_IsContained_AndReportedNowhere()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var eventFailures = new List<EventHandlerFailure>();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler with
        {
            DefaultExceptionHandler = eventFailures.Add,
        })
        {
            Name = "OwnExceptionHandlerThrows",
        };
        using var defaultHandler = new DefaultExceptionHandlerScope(source.Name);
        var exceptionHandlerRuns = 0;

        events.Subscribe(source, ThrowForCall, onException: (exception, call) =>
        {
            exceptionHandlerRuns++;
            throw new InvalidOperationException("the exception handler failed too");
        });
        //FakeComponent runs renderer work inline, so the exception handler has finished when the raise returns
        await source.RaiseAsync(8);

        await Assert.That(exceptionHandlerRuns).IsEqualTo(1);
        await Assert.That(eventFailures.Count).IsEqualTo(0);
        await Assert.That(defaultHandler.Failures.Count).IsEqualTo(0);
        await Assert.That(component.DispatchedExceptions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task HandlerException_FollowsTheEventsOwnRouting_WhenNoDispatchExceptionAsyncIsSupplied()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions(dispatchExceptions: false);
        var failures = new List<EventHandlerFailure>();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler with
        {
            DefaultExceptionHandler = failures.Add,
        });

        var subscription = events.Subscribe(source, ThrowForCall);
        await source.RaiseAsync(5);

        await Assert.That(failures.Count).IsEqualTo(1);
        await Assert.That(failures[0].Stage).IsEqualTo(EventFailureStage.Handler);
        await Assert.That(failures[0].SubscriptionId).IsEqualTo(subscription!.Id);
        await Assert.That(failures[0].Exception.Message).IsEqualTo("handler failed for 5");
    }

    [Test]
    [NotInParallel(DefaultExceptionHandlerScope.NotInParallelKey)]
    public async Task HandlerExceptionThatCanNoLongerReachTheComponent_GoesToTheEventsOwnExceptionHandler()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var eventFailures = new EventFailureRecorder();
        var source = new IntercomEvent<int>(eventFailures.EventOptions)
        {
            Name = "HandlerFailsAfterComponentDisposal",
        };
        using var processDefaultHandler = new DefaultExceptionHandlerScope(source.Name);
        var handlerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = events.Subscribe(source, async call =>
        {
            await handlerRelease.Task;
            throw new InvalidOperationException("failed after the component was disposed");
        });

        var raise = source.RaiseAsync(6);
        await events.DisposeAsync();
        handlerRelease.SetResult();
        await raise;
        var failure = await eventFailures.WaitForFirstFailureAsync();

        await Assert.That(component.DispatchedExceptions.Count).IsEqualTo(0);
        await Assert.That(eventFailures.Failures.Count).IsEqualTo(1);
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.Handler);
        await Assert.That(failure.EventName).IsEqualTo(source.Name);
        await Assert.That(failure.SubscriptionId).IsEqualTo(subscription!.Id);
        await Assert.That(failure.Call).IsNotNull();
        await Assert.That(failure.Exception.Message).IsEqualTo("failed after the component was disposed");
        //the event has options of its own, so EventingOptions.Default is not involved
        await Assert.That(processDefaultHandler.Failures.Count).IsEqualTo(0);
    }

    [Test]
    [NotInParallel(DefaultExceptionHandlerScope.NotInParallelKey)]
    public async Task HandlerExceptionThatCanNoLongerReachTheComponent_OfAnEventWithoutOwnOptions_GoesToTheDefaultInEffectAtThatMoment()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>
        {
            Name = "DefaultOptionsHandlerFailsAfterComponentDisposal",
        };
        var handlerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = events.Subscribe(source, async call =>
        {
            await handlerRelease.Task;
            throw new InvalidOperationException("failed after the component was disposed");
        });

        var raise = source.RaiseAsync(7);
        await events.DisposeAsync();
        //replaced while the handler waits: the event's options are read when the failure is reported, not when the
        //subscription was made or the raise started
        using var processDefaultHandler = new DefaultExceptionHandlerScope(source.Name);
        handlerRelease.SetResult();
        var failure = await processDefaultHandler.WaitForFirstFailureAsync();
        //EventingOptions.Default may have moved the slow handler to the background, so the raise is awaited after the failure
        await raise.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(component.DispatchedExceptions.Count).IsEqualTo(0);
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.Handler);
        await Assert.That(failure.SubscriptionId).IsEqualTo(subscription!.Id);
        await Assert.That(failure.Exception.Message).IsEqualTo("failed after the component was disposed");
    }

    [Test]
    public async Task OwnExceptionHandler_DoesNotRunAfterDisposal_AndTheFailureGoesToTheEventsExceptionHandler()
    {
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var eventFailures = new EventFailureRecorder();
        var source = new IntercomEvent<int>(eventFailures.EventOptions);
        var handlerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exceptionHandlerRuns = 0;
        var subscription = events.Subscribe(source, async call =>
        {
            await handlerRelease.Task;
            throw new InvalidOperationException("failed after the component was disposed");
        }, onException: (exception, call) => exceptionHandlerRuns++);

        var raise = source.RaiseAsync(9);
        await events.DisposeAsync();
        handlerRelease.SetResult();
        await raise;
        var failure = await eventFailures.WaitForFirstFailureAsync();

        await Assert.That(exceptionHandlerRuns).IsEqualTo(0);
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.Handler);
        await Assert.That(failure.SubscriptionId).IsEqualTo(subscription!.Id);
        await Assert.That(failure.Exception.Message).IsEqualTo("failed after the component was disposed");
    }

    [Test]
    public async Task OwnExceptionHandlerQueuedForTheRenderer_IsSkipped_WhenTheComponentIsDisposedBeforeItRuns()
    {
        var component = new FakeComponent
        {
            HoldWorkItems = true,
        };
        var events = component.CreateSubscriptions();
        var eventFailures = new EventFailureRecorder();
        var source = new IntercomEvent<int>(eventFailures.EventOptions);
        var exceptionHandlerRuns = 0;
        events.Subscribe(source, ThrowForCall, onException: (exception, call) => exceptionHandlerRuns++);

        var raise = source.RaiseAsync(10);
        //runs the held handler, which fails; the core hands the exception to the route before the raise completes, and the
        //route queues the exception handler for the renderer
        await component.RunHeldWorkItemsAsync();
        await raise;
        var queuedExceptionHandlers = component.HeldWorkItemCount;
        await events.DisposeAsync();
        await component.RunHeldWorkItemsAsync();
        var failure = await eventFailures.WaitForFirstFailureAsync();

        await Assert.That(queuedExceptionHandlers).IsEqualTo(1);
        await Assert.That(exceptionHandlerRuns).IsEqualTo(0);
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.Handler);
        await Assert.That(failure.Exception.Message).IsEqualTo("handler failed for 10");
    }

    [Test]
    public async Task RendererThatRefusesWorkAfterTheComponentWasDisposed_IsNotReportedAsAHandlerFailure()
    {
        var failures = new List<EventHandlerFailure>();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler with
        {
            DefaultExceptionHandler = failures.Add,
        });
        ComponentEventSubscriptions? events = null;
        var handlerCalls = 0;
        //a renderer that is torn down together with its component, as when a Blazor Server circuit closes
        events = new ComponentEventSubscriptions(workItem =>
        {
            events!.Dispose();
            throw new ObjectDisposedException("renderer");
        }, () => { });
        events.Subscribe(source, call => handlerCalls++);

        await source.RaiseAsync(7);

        await Assert.That(handlerCalls).IsEqualTo(0);
        await Assert.That(failures.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CallQueuedForTheRenderer_IsSkipped_WhenItsSubscriptionIsDisposedBeforeItRuns()
    {
        //the event skips a handler it has not started once the subscription is gone; a call already queued for a busy
        //renderer must be dropped the same way, or the handler receives a call after Dispose()
        var component = new FakeComponent
        {
            HoldWorkItems = true,
        };
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var handlerCalls = 0;
        var subscription = events.Subscribe(source, call => handlerCalls++);

        var raise = source.RaiseAsync(1);
        var queuedWorkItems = component.HeldWorkItemCount;
        subscription!.Dispose();
        await component.RunHeldWorkItemsAsync();
        await raise;

        await Assert.That(queuedWorkItems).IsEqualTo(1);
        await Assert.That(handlerCalls).IsEqualTo(0);
        await Assert.That(component.RenderCount).IsEqualTo(0);
        await Assert.That(source.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task CallQueuedForTheRenderer_IsSkipped_WhenResubscribeMovedToAnotherEvent()
    {
        //an EventListener whose Event parameter changed must not pass a queued call of the old event to its callback
        var component = new FakeComponent
        {
            HoldWorkItems = true,
        };
        var events = component.CreateSubscriptions();
        var oldEvent = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler)
        {
            Name = "OldEvent",
        };
        var newEvent = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler)
        {
            Name = "NewEvent",
        };
        var receivedFrom = new List<string>();
        Action<EventCall<int>> recordEventName = call => receivedFrom.Add(call.Metadata.EventName);
        var subscription = events.Resubscribe(null, oldEvent, recordEventName);

        var oldRaise = oldEvent.RaiseAsync(1);
        subscription = events.Resubscribe(subscription, newEvent, recordEventName);
        await component.RunHeldWorkItemsAsync();
        await oldRaise;
        var newRaise = newEvent.RaiseAsync(2);
        await component.RunHeldWorkItemsAsync();
        await newRaise;

        await Assert.That(string.Join(",", receivedFrom)).IsEqualTo("NewEvent");
        await Assert.That(oldEvent.SubscriberCount).IsEqualTo(0);
        await Assert.That(subscription!.IsActive).IsTrue();
    }

    [Test]
    public async Task CancelledHandlerTask_IsNeitherDispatchedNorHandledNorReported()
    {
        //Blazor ignores a cancelled UI event handler task; dispatching it would end a Server circuit for an HttpClient timeout
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var eventFailures = new EventFailureRecorder();
        var source = new IntercomEvent<int>(eventFailures.EventOptions);
        var ownExceptionHandlerRuns = 0;
        //a gate rather than Task.Yield: a yielded handler can finish on the thread pool before the helper checks whether it
        //is still running, which skips the render at its first await on a busy machine
        var handlerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<EventCall<int>, Task> timeOutLikeHttpClient = async call =>
        {
            await handlerRelease.Task;
            throw new TaskCanceledException("simulated HttpClient timeout");
        };
        events.Subscribe(source, timeOutLikeHttpClient);
        events.Subscribe(source, timeOutLikeHttpClient, onException: (exception, call) => ownExceptionHandlerRuns++);

        var raise = source.RaiseAsync(1);
        handlerRelease.SetResult();
        await raise;

        await Assert.That(component.DispatchedExceptions.Count).IsEqualTo(0);
        await Assert.That(ownExceptionHandlerRuns).IsEqualTo(0);
        await Assert.That(eventFailures.Failures.Count).IsEqualTo(0);
        //each handler rendered at its first await and, like a cancelled Blazor UI event handler, not again afterwards
        await Assert.That(component.RenderCount).IsEqualTo(2);
    }

    [Test]
    public async Task HandlerCancelledByTheComponentsOwnDisposal_IsNotReportedToTheEvent()
    {
        //the canonical cleanup cancels a component-owned token in DisposeAsyncCore while a handler still awaits it
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var eventFailures = new EventFailureRecorder();
        var source = new IntercomEvent<int>(eventFailures.EventOptions);
        using var componentLifetime = new CancellationTokenSource();
        var handlerWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        events.Subscribe(source, async call =>
        {
            handlerWaiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, componentLifetime.Token);
        });

        var raise = source.RaiseAsync(1);
        await handlerWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await events.DisposeAsync();
        await componentLifetime.CancelAsync();
        await raise.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(eventFailures.Failures.Count).IsEqualTo(0);
        await Assert.That(component.DispatchedExceptions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OperationCanceledExceptionThrownBeforeTheHandlerReturns_IsDispatchedLikeAnyOtherThrow()
    {
        //as in Blazor, only a cancelled task counts as cancellation; a synchronous throw is a failure of the handler
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);

        events.Subscribe(source, ThrowCancellationForCall);
        await source.RaiseAsync(2);

        await Assert.That(component.DispatchedExceptions.Count).IsEqualTo(1);
        await Assert.That(component.DispatchedExceptions[0]).IsTypeOf<OperationCanceledException>();
    }

    [Test]
    public async Task Resubscribe_ReplacesACurrentSubscriptionThatWasRemoved()
    {
        //a handle disposed by the component (Subscribe's docs allow it) must not be kept forever for the same event
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        IIntercomEvent<EventCall<int>> source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var handlerCalls = 0;
        Action<EventCall<int>> countCall = call => handlerCalls++;

        var first = events.Resubscribe(null, source, countCall);
        first!.Dispose();
        var second = events.Resubscribe(first, source, countCall);
        await ((IntercomEvent<int>)source).RaiseAsync(1);

        await Assert.That(second).IsNotSameReferenceAs(first);
        await Assert.That(second!.IsActive).IsTrue();
        await Assert.That(source.SubscriberCount).IsEqualTo(1);
        await Assert.That(handlerCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Resubscribe_ThatCannotSubscribeToTheNewEvent_KeepsTheCurrentSubscription()
    {
        //moving to an event that throws used to remove the live subscription first, leaving the caller's field holding a
        //dead subscription that a move back to the live event then kept
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var liveEvent = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var disposedEvent = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        await disposedEvent.DisposeAsync();
        var takenEvent = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler with
        {
            AllowMultipleSubscribers = false,
        });
        takenEvent.Subscribe(_ => { });
        var receivedPayloads = new List<int>();
        Action<EventCall<int>> recordPayload = call => receivedPayloads.Add(call.Payload);
        var current = events.Resubscribe(null, liveEvent, recordPayload);

        await Assert.That(() => events.Resubscribe(current, disposedEvent, recordPayload)).Throws<ObjectDisposedException>();
        await Assert.That(() => events.Resubscribe(current, takenEvent, recordPayload)).Throws<InvalidOperationException>();
        var afterMovingBack = events.Resubscribe(current, liveEvent, recordPayload);
        await liveEvent.RaiseAsync(5);

        await Assert.That(afterMovingBack).IsSameReferenceAs(current);
        await Assert.That(current!.IsActive).IsTrue();
        await Assert.That(liveEvent.SubscriberCount).IsEqualTo(1);
        await Assert.That(takenEvent.SubscriberCount).IsEqualTo(1);
        await Assert.That(string.Join(",", receivedPayloads)).IsEqualTo("5");
    }

    [Test]
    public async Task CombinedDelegate_IsInvokedAsOneDelegate()
    {
        //documented: the helper does not split a + b the way the event's own Subscribe does, so a throwing part stops the rest
        var component = new FakeComponent();
        var events = component.CreateSubscriptions();
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var secondPartCalls = 0;
        Action<EventCall<int>> firstPart = ThrowForCall;
        Action<EventCall<int>> secondPart = call => secondPartCalls++;

        events.Subscribe(source, firstPart + secondPart);
        await source.RaiseAsync(6);

        await Assert.That(secondPartCalls).IsEqualTo(0);
        await Assert.That(component.DispatchedExceptions.Count).IsEqualTo(1);
    }

    private static void ThrowForCall(EventCall<int> call)
    {
        throw new InvalidOperationException($"handler failed for {call.Payload}");
    }

    private static void ThrowCancellationForCall(EventCall<int> call)
    {
        throw new OperationCanceledException($"handler gave up on {call.Payload}");
    }
}
