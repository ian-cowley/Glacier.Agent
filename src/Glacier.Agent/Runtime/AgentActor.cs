namespace Glacier.Agent.Runtime;

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public interface IAgentActor : IDisposable
{
    string Id { get; }
    ActorLifecycleState State { get; }
    IActorSupervisor? Supervisor { get; set; }
    ValueTask SendAsync(AgentMessage message, CancellationToken ct = default);
    bool TrySend(AgentMessage message);
    void Start();
    Task StopAsync();
}

/// <summary>
/// Lightweight reactive agent actor with an asynchronous mailbox and supervisor hierarchy integration.
/// </summary>
public class AgentActor : IAgentActor
{
    private readonly Channel<AgentMessage> _mailbox;
    private readonly CancellationTokenSource _cts = new();
    private Task? _processingLoopTask;
    private int _state = (int)ActorLifecycleState.Uninitialized;

    public string Id { get; }
    public ActorLifecycleState State => (ActorLifecycleState)_state;
    public IActorSupervisor? Supervisor { get; set; }
    public MailboxBackpressureStrategy BackpressureStrategy { get; }

    public AgentActor(
        string id,
        int mailboxCapacity = 1000,
        MailboxBackpressureStrategy strategy = MailboxBackpressureStrategy.Wait)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        BackpressureStrategy = strategy;

        if (strategy == MailboxBackpressureStrategy.Unbounded)
        {
            _mailbox = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }
        else
        {
            BoundedChannelFullMode fullMode = strategy switch
            {
                MailboxBackpressureStrategy.DropOldest => BoundedChannelFullMode.DropOldest,
                MailboxBackpressureStrategy.DropNewest => BoundedChannelFullMode.DropWrite,
                _ => BoundedChannelFullMode.Wait
            };

            _mailbox = Channel.CreateBounded<AgentMessage>(new BoundedChannelOptions(mailboxCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = fullMode
            });
        }
    }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, (int)ActorLifecycleState.Starting, (int)ActorLifecycleState.Uninitialized) == (int)ActorLifecycleState.Uninitialized ||
            Interlocked.CompareExchange(ref _state, (int)ActorLifecycleState.Starting, (int)ActorLifecycleState.Paused) == (int)ActorLifecycleState.Paused)
        {
            _processingLoopTask = Task.Run(RunLoopAsync);
        }
    }

    public async ValueTask SendAsync(AgentMessage message, CancellationToken ct = default)
    {
        if (State == ActorLifecycleState.Stopped || State == ActorLifecycleState.Faulted)
        {
            throw new InvalidOperationException($"Cannot send message to actor '{Id}' in state '{State}'.");
        }

        await _mailbox.Writer.WriteAsync(message, ct).ConfigureAwait(false);
    }

    public bool TrySend(AgentMessage message)
    {
        if (State == ActorLifecycleState.Stopped || State == ActorLifecycleState.Faulted)
        {
            return false;
        }

        return _mailbox.Writer.TryWrite(message);
    }

    private async Task RunLoopAsync()
    {
        try
        {
            await OnStartAsync(_cts.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _state, (int)ActorLifecycleState.Active);

            ChannelReader<AgentMessage> reader = _mailbox.Reader;
            while (!_cts.Token.IsCancellationRequested && await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out AgentMessage? msg))
                {
                    try
                    {
                        await OnMessageAsync(msg, _cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await HandleMessageFaultAsync(ex, msg).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _state, (int)ActorLifecycleState.Faulted);
            Supervisor?.HandleFailure(this, ex);
        }
        finally
        {
            if (State != ActorLifecycleState.Faulted)
            {
                Interlocked.Exchange(ref _state, (int)ActorLifecycleState.Stopped);
            }
            await OnStopAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleMessageFaultAsync(Exception ex, AgentMessage msg)
    {
        SupervisionDirective directive = Supervisor?.HandleFailure(this, ex) ?? SupervisionDirective.Resume;
        switch (directive)
        {
            case SupervisionDirective.Resume:
                // Continue processing next messages
                break;
            case SupervisionDirective.Restart:
                Interlocked.Exchange(ref _state, (int)ActorLifecycleState.Starting);
                await OnRestartAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _state, (int)ActorLifecycleState.Active);
                break;
            case SupervisionDirective.Stop:
                Interlocked.Exchange(ref _state, (int)ActorLifecycleState.Stopped);
                _cts.Cancel();
                break;
            case SupervisionDirective.Escalate:
                Interlocked.Exchange(ref _state, (int)ActorLifecycleState.Faulted);
                _cts.Cancel();
                throw ex;
        }
    }

    protected virtual Task OnStartAsync(CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnMessageAsync(AgentMessage msg, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnRestartAsync() => Task.CompletedTask;
    protected virtual Task OnStopAsync() => Task.CompletedTask;

    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _state, (int)ActorLifecycleState.Stopped);
        _mailbox.Writer.TryComplete();
        _cts.Cancel();

        if (_processingLoopTask != null)
        {
            try
            {
                await _processingLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
