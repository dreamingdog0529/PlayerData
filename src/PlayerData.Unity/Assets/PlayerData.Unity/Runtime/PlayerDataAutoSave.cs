using System;
using System.Threading;
using UnityEngine;

namespace PlayerData.Unity;

// Commits a dirty ISaveSession on pause/quit and optionally on an interval.
// Attach to a bootstrap GameObject after constructing the session.
[DisallowMultipleComponent]
public sealed class PlayerDataAutoSave : MonoBehaviour
{
    [SerializeField] private float _intervalSeconds;
    [SerializeField] private bool _commitOnPause = true;
    [SerializeField] private bool _commitOnQuit = true;

    private ISaveSession? _session;
    private float _elapsed;
    private int _commitGate;

    public float IntervalSeconds
    {
        get => _intervalSeconds;
        set => _intervalSeconds = value;
    }

    public bool CommitOnPause
    {
        get => _commitOnPause;
        set => _commitOnPause = value;
    }

    public bool CommitOnQuit
    {
        get => _commitOnQuit;
        set => _commitOnQuit = value;
    }

    public void Bind(ISaveSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    private void Update()
    {
        if (_session is null || _intervalSeconds <= 0f) return;
        if (!_session.IsDirty) return;

        _elapsed += Time.unscaledDeltaTime;
        if (_elapsed < _intervalSeconds) return;
        _elapsed = 0f;
        CommitFireAndForget();
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus && _commitOnPause)
            CommitFireAndForget();
    }

    private void OnApplicationQuit()
    {
        if (_commitOnQuit)
            CommitFireAndForget();
    }

    private void CommitFireAndForget()
    {
        if (_session is null || !_session.IsDirty) return;
        if (Interlocked.CompareExchange(ref _commitGate, 1, 0) != 0) return;

        var session = _session;
        _ = CommitAsync(session);
    }

    private async System.Threading.Tasks.Task CommitAsync(ISaveSession session)
    {
        try
        {
            await session.CommitAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _commitGate, 0);
        }
    }
}
