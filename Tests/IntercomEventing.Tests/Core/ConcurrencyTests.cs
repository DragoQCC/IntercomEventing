using System.Collections.Concurrent;
using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// Subscribing, unsubscribing, raising and disposing from many threads at once. The subscriber list is a copy-on-write array
/// swapped under a lock and every raise reads one snapshot, so nothing is lost, corrupted or thrown. Every stress loop has a
/// fixed size or a fixed duration.
/// </summary>
[NotInParallel("CpuHeavy")]
public class ConcurrencyTests
{
    private static readonly TimeSpan StressDuration = TimeSpan.FromSeconds(1);

    [Test]
    public async Task ParallelSubscribeAndDispose_WhileRaising_LosesNothing()
    {
        //S3 (harness): 1.2.0's HashSet threw "Collection was modified", lost subscriptions and once crashed the finalizer
        var evt = new IntercomEvent<int>(new EventingOptions());
        var raiseFailures = 0;
        var subscribeFailures = 0;
        var stopRaising = false;
        var raiser = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stopRaising))
            {
                try
                {
                    await evt.RaiseAsync(1);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref raiseFailures);
                }
            }
        });
        var subscriptions = new EventSubscription<EventCall<int>>[5000];

        Parallel.For(0, subscriptions.Length, index =>
        {
            try
            {
                subscriptions[index] = evt.Subscribe(_ => { });
            }
            catch (Exception)
            {
                Interlocked.Increment(ref subscribeFailures);
            }
        });
        var countAfterSubscribing = evt.SubscriberCount;
        Parallel.For(0, subscriptions.Length, index => subscriptions[index].Dispose());
        Volatile.Write(ref stopRaising, true);
        await raiser.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(subscribeFailures).IsEqualTo(0);
        await Assert.That(raiseFailures).IsEqualTo(0);
        await Assert.That(countAfterSubscribing).IsEqualTo(5000);
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentSubscribeCalls_AllRegister()
    {
        //S3: 20 rounds of 5000 concurrent Subscribe calls lost 54% of the subscriptions in 1.2.0
        var roundsWithTheWrongCount = 0;
        var subscribeFailures = 0;
        for (var round = 0; round < 5; round++)
        {
            var evt = new IntercomEvent<int>(new EventingOptions());
            Parallel.For(0, 5000, _ =>
            {
                try
                {
                    evt.Subscribe(_ => { });
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref subscribeFailures);
                }
            });
            if (evt.SubscriberCount != 5000)
            {
                roundsWithTheWrongCount++;
            }
        }

        await Assert.That(subscribeFailures).IsEqualTo(0);
        await Assert.That(roundsWithTheWrongCount).IsEqualTo(0);
    }

    [Test]
    public async Task TwoThreadsUnsubscribingATwiceSubscribedDelegate_EachRemoveOne()
    {
        //native -= semantics under concurrency: the search and the removal happen under one lock, so each of two calls removes
        //a different subscription. Dedicated threads released together by a barrier make the two calls of every trial overlap
        const int numberOfTrials = 5000;
        Action<EventCall<int>> handler = _ => { };
        var events = new IntercomEvent<int>[numberOfTrials];
        for (var trial = 0; trial < numberOfTrials; trial++)
        {
            events[trial] = new IntercomEvent<int>(new EventingOptions());
            events[trial].Subscribe(handler);
            events[trial].Subscribe(handler);
        }
        var removed = new bool[numberOfTrials, 2];

        await LockstepThreads.RunAsync(threadCount: 2, rounds: numberOfTrials, (threadIndex, trial) =>
        {
            removed[trial, threadIndex] = events[trial].Unsubscribe(handler);
        }).WaitAsync(Eventually.DefaultTimeout);

        var trialsWithASubscriptionLeft = events.Count(trialEvent => trialEvent.SubscriberCount != 0);
        var unsubscribesThatRemovedNothing = removed.Cast<bool>().Count(removedOne => !removedOne);
        await Assert.That(trialsWithASubscriptionLeft).IsEqualTo(0);
        await Assert.That(unsubscribesThatRemovedNothing).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentPlusAndMinusEqualsOfDistinctHandlers_LeaveExactlyTheAddedHandlers()
    {
        //documented: concurrent += and -= never lose or double up a subscription. Each is one swap of the subscriber array under
        //the lock; a removal that rebuilt the array from a stale copy would drop a concurrent addition or bring back a
        //concurrent removal. Two threads remove the original handlers while two others add new ones, all in lockstep
        const int handlersPerThread = 500;
        var evt = new IntercomEvent<int>(new EventingOptions());
        var handlersThatRan = new ConcurrentBag<int>();
        var originalHandlers = CreateNumberedHandlers(firstNumber: 0, count: 2 * handlersPerThread, handlersThatRan);
        var addedHandlers = CreateNumberedHandlers(firstNumber: 2 * handlersPerThread, count: 2 * handlersPerThread, handlersThatRan);
        foreach (var originalHandler in originalHandlers)
        {
            evt.Raised += originalHandler;
        }

        //threads 0 and 1 remove, threads 2 and 3 add; each thread works through its own half of its handler array
        await LockstepThreads.RunAsync(threadCount: 4, rounds: handlersPerThread, (threadIndex, round) =>
        {
            var isARemovingThread = threadIndex < 2;
            var firstIndexOfThisThreadsHalf = (threadIndex % 2) * handlersPerThread;
            var handlerIndex = firstIndexOfThisThreadsHalf + round;
            if (isARemovingThread)
            {
                evt.Raised -= originalHandlers[handlerIndex];
            }
            else
            {
                evt.Raised += addedHandlers[handlerIndex];
            }
        }).WaitAsync(Eventually.DefaultTimeout);
        await evt.RaiseAsync(1);

        var expectedHandlerNumbers = Enumerable.Range(2 * handlersPerThread, 2 * handlersPerThread).ToArray();
        var handlerNumbersThatRan = handlersThatRan.Order().ToArray();
        await Assert.That(evt.SubscriberCount).IsEqualTo(2 * handlersPerThread);
        await Assert.That(handlerNumbersThatRan).IsEquivalentTo(expectedHandlerNumbers, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SubscribeInsideASequentialHandler_TakesEffectFromTheNextRaise()
    {
        //S3b (harness): a handler that subscribed made a sequential 1.2.0 raise throw "Collection was modified"
        var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = DispatchMode.Sequential });
        var lateSubscriberCalls = 0;
        var subscribedInHandler = false;
        evt.Subscribe(_ =>
        {
            if (!subscribedInHandler)
            {
                subscribedInHandler = true;
                evt.Subscribe(_ =>
                {
                    Interlocked.Increment(ref lateSubscriberCalls);
                });
            }
        });
        evt.Subscribe(_ => { });

        await evt.RaiseAsync(1);
        var callsAfterTheFirstRaise = Volatile.Read(ref lateSubscriberCalls);
        await evt.RaiseAsync(2);

        await Assert.That(callsAfterTheFirstRaise).IsEqualTo(0);
        await Assert.That(Volatile.Read(ref lateSubscriberCalls)).IsEqualTo(1);
    }

    [Test]
    public async Task YieldingParallelHandlersThatSubscribe_LoseNoSubscription()
    {
        //S3: 16 handlers that yield and then subscribe, over 100 raises, ended with 1603 instead of 1616 subscriptions in 1.2.0.
        //The handler timeout runs on a fake clock that never moves, so a yield delayed by a loaded machine cannot move a
        //handler to the background and let the raise return before that handler subscribed
        var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = DispatchMode.Parallel, TimeProvider = new FakeTimeProvider() });
        for (var handlerNumber = 0; handlerNumber < 16; handlerNumber++)
        {
            evt.Subscribe(async _ =>
            {
                //after the yield the 16 handlers subscribe from different thread-pool threads at the same time
                await Task.Yield();
                evt.Subscribe(_ => { });
            });
        }

        for (var raiseNumber = 0; raiseNumber < 100; raiseNumber++)
        {
            await evt.RaiseAsync(raiseNumber).WaitAsync(Eventually.DefaultTimeout);
        }

        //the added handlers do not subscribe themselves, so each raise adds exactly 16
        await Assert.That(evt.SubscriberCount).IsEqualTo(16 + 16 * 100);
    }

    [Test]
    public async Task UnsubscribingInsideAHandler_DoesNotDisturbTheRunningRaise()
    {
        //S3: removing itself or another subscriber from a sequential handler was the refuted part in 1.2.0; still true in 2.0
        var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = DispatchMode.Sequential });
        EventSubscription<EventCall<int>>? otherSubscription = null;
        EventSubscription<EventCall<int>>? selfSubscription = null;
        var otherHandlerCalls = 0;
        selfSubscription = evt.Subscribe(_ =>
        {
            selfSubscription!.Dispose();
            otherSubscription!.Dispose();
        });
        otherSubscription = evt.Subscribe(_ =>
        {
            Interlocked.Increment(ref otherHandlerCalls);
        });

        var raising = evt.RaiseAsync(1);
        await raising;

        await Assert.That(raising.IsCompletedSuccessfully).IsTrue();
        //the other subscription was removed before its handler started, so this raise skipped it
        await Assert.That(Volatile.Read(ref otherHandlerCalls)).IsEqualTo(0);
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task RaisesAreNotSerialized()
    {
        //documented: a second raise does not wait for the first raise's handlers
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = null });
        var firstRaiseGate = Gate.Create();
        evt.Subscribe(async call =>
        {
            if (call.Payload == 1)
            {
                await firstRaiseGate.Task;
            }
        });

        var firstRaise = evt.RaiseAsync(1);
        var secondRaise = evt.RaiseAsync(2);
        var secondFinished = await Eventually.CompletesAsync(secondRaise);
        var firstStillRunning = !firstRaise.IsCompleted;
        firstRaiseGate.SetResult();
        await firstRaise.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(secondFinished).IsTrue();
        await Assert.That(firstStillRunning).IsTrue();
    }

    [Test]
    public async Task RaisesRacingDisposeAsync_FailOnlyWithObjectDisposedException()
    {
        //harness D1: raises that lose the race against disposal fail cleanly; handlers that honour the token are not failures
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            StartNextEventHandlerAfter = TimeSpan.FromMilliseconds(20),
            DefaultExceptionHandler = failures.Record,
        });
        evt.Subscribe(async call => await Task.Delay(100, call.CancellationToken));
        evt.Subscribe(_ => { });
        var raiseExceptionTypes = new ConcurrentDictionary<Type, int>();
        var stopRaising = false;
        var raisers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!Volatile.Read(ref stopRaising))
            {
                try
                {
                    await evt.RaiseAsync(1);
                }
                catch (Exception exception)
                {
                    raiseExceptionTypes.AddOrUpdate(exception.GetType(), 1, (_, count) => count + 1);
                    await Task.Delay(1);
                }
            }
        })).ToArray();

        await Task.Delay(150);
        var disposed = await Eventually.CompletesAsync(evt.DisposeAsync().AsTask());
        //keep raising until at least one raise has lost the race, so the exception check below is never vacuous
        var aRaiseFailedAfterDisposal = await Eventually.IsTrueAsync(() => raiseExceptionTypes.ContainsKey(typeof(ObjectDisposedException)));
        Volatile.Write(ref stopRaising, true);
        await Task.WhenAll(raisers).WaitAsync(Eventually.DefaultTimeout);
        var secondDisposeFinished = evt.DisposeAsync().IsCompletedSuccessfully;

        await Assert.That(disposed).IsTrue();
        await Assert.That(aRaiseFailedAfterDisposal).IsTrue();
        await Assert.That(secondDisposeFinished).IsTrue();
        await Assert.That(raiseExceptionTypes.Keys).All(exceptionType => exceptionType == typeof(ObjectDisposedException));
        await Assert.That(raiseExceptionTypes.Keys.Count).IsEqualTo(1);
        await Assert.That(failures.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SubscribeUnsubscribeRaiseAndDispose_RunTogetherCleanly()
    {
        //bounded stress over every route at once; only failures the test planted may appear, and disposal ends with nothing left
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            StartNextEventHandlerAfter = TimeSpan.FromMilliseconds(5),
            MaxConcurrentHandlers = 8,
            DefaultExceptionHandler = failures.Record,
        });
        var unexpectedExceptions = new ConcurrentQueue<Exception>();
        var stopChurning = false;
        var stopRaising = false;
        var disposalStarted = false;
        Func<EventCall<int>, Task> plantedFailure = async call =>
        {
            await Task.Yield();
            if (call.Payload % 7 == 0)
            {
                throw new InvalidOperationException("planted failure");
            }
        };

        var raisers = Enumerable.Range(0, 3).Select(raiserNumber => Task.Run(async () =>
        {
            var payload = raiserNumber;
            while (!Volatile.Read(ref stopRaising))
            {
                payload++;
                await RunExpectingOnlyDisposalAsync(() => evt.RaiseAsync(payload), unexpectedExceptions, () => Volatile.Read(ref disposalStarted));
            }
        })).ToArray();
        var churners = new[]
        {
            Task.Run(async () =>
            {
                while (!Volatile.Read(ref stopChurning))
                {
                    var subscription = evt.Subscribe(plantedFailure);
                    await Task.Yield();
                    subscription.Dispose();
                }
            }),
            Task.Run(async () =>
            {
                Func<EventCall<int>, Task> handler = async _ => await Task.Yield();
                while (!Volatile.Read(ref stopChurning))
                {
                    evt.Raised += handler;
                    await Task.Yield();
                    evt.Raised -= handler;
                }
            }),
            Task.Run(async () =>
            {
                while (!Volatile.Read(ref stopChurning))
                {
                    var subscription = await evt.SubscribeAsync(_ => { }, new()
                    {
                        OnSubscribed = async _ => await Task.Yield(),
                        OnUnsubscribed = async _ => await Task.Yield(),
                    });
                    await subscription.DisposeAsync();
                }
            }),
            Task.Run(async () =>
            {
                Action<EventCall<int>> handler = _ => { };
                while (!Volatile.Read(ref stopChurning))
                {
                    evt.Subscribe(handler);
                    await Task.Yield();
                    evt.Unsubscribe(handler);
                }
            }),
        };

        await Task.Delay(StressDuration);
        Volatile.Write(ref stopChurning, true);
        await CaptureUnexpectedAsync(Task.WhenAll(churners).WaitAsync(Eventually.DefaultTimeout), unexpectedExceptions);
        Volatile.Write(ref disposalStarted, true);
        var disposed = await Eventually.CompletesAsync(evt.DisposeAsync().AsTask());
        Volatile.Write(ref stopRaising, true);
        await CaptureUnexpectedAsync(Task.WhenAll(raisers).WaitAsync(Eventually.DefaultTimeout), unexpectedExceptions);
        var backgroundDrained = await Eventually.IsTrueAsync(() => evt.BackgroundWorkCount == 0);

        await Assert.That(unexpectedExceptions).IsEmpty();
        await Assert.That(disposed).IsTrue();
        await Assert.That(backgroundDrained).IsTrue();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
        await Assert.That(failures.Failures).All(failure => failure.Exception.Message == "planted failure");
    }

    //distinct delegates (each closes over its own number) that record their number when they run
    private static Func<EventCall<int>, Task>[] CreateNumberedHandlers(int firstNumber, int count, ConcurrentBag<int> handlersThatRan)
    {
        var handlers = new Func<EventCall<int>, Task>[count];
        for (var index = 0; index < count; index++)
        {
            var handlerNumber = firstNumber + index;
            handlers[index] = _ =>
            {
                handlersThatRan.Add(handlerNumber);
                return Task.CompletedTask;
            };
        }
        return handlers;
    }

    private static async Task RunExpectingOnlyDisposalAsync(Func<Task> operation, ConcurrentQueue<Exception> unexpectedExceptions,
        Func<bool> disposalStarted)
    {
        try
        {
            await operation();
        }
        catch (ObjectDisposedException) when (disposalStarted())
        {
            //losing the race against disposal is expected
        }
        catch (Exception exception)
        {
            unexpectedExceptions.Enqueue(exception);
        }
    }

    private static async Task CaptureUnexpectedAsync(Task work, ConcurrentQueue<Exception> unexpectedExceptions)
    {
        try
        {
            await work;
        }
        catch (Exception exception)
        {
            unexpectedExceptions.Enqueue(exception);
        }
    }
}
