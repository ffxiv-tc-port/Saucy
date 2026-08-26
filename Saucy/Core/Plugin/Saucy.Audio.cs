using NAudio.Wave;
using Saucy.Framework;
using System;
using System.IO;
using System.Threading.Tasks;
namespace Saucy;

public sealed partial class Saucy
{
    private static void ScheduleLogout() =>
        _ = PerformLogout().ContinueWith(
            task =>
            {
                if (task.Exception is not null)
                {
                    Svc.Log.Error(task.Exception, "[Saucy] Logout after completion failed");
                }
            },
            TaskScheduler.Default);

    private static async Task PerformLogout()
    {
        SelectYesnoHelper.ArmForYes(TimeSpan.FromSeconds(15));
        await Svc.Framework.RunOnTick(TriadRunSession.Logout, TimeSpan.FromMilliseconds(2000));
        await Svc.Framework.RunOnTick(TriadRunSession.SelectYesLogout, TimeSpan.FromMilliseconds(3500));
    }

    private void PlaySound()
    {
        // Reading/decoding the mp3 and initializing the playback device are blocking calls;
        // this is called from the Framework tick (Saucy.Tick.cs), so run it off the main
        // thread. NAudio's WaveOutEvent isn't tied to the render thread the way Dalamud's
        // D3D11 texture APIs are, so backgrounding it entirely is safe.
        Task.Run(() =>
        {
            lock (_lockObj)
            {
                DisposeAudio();

                var sound = C.SelectedSound;
                var path = Path.Combine(Svc.PluginInterface.AssemblyLocation.Directory!.FullName, "Sounds", $"{sound}.mp3");
                if (!File.Exists(path))
                {
                    return;
                }

                _currentReader = new(path);
                _currentWaveOut = new();
                _currentWaveOut.PlaybackStopped += OnPlaybackStopped;
                _currentWaveOut.Init(_currentReader);
                _currentWaveOut.Play();
            }
        });
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lockObj)
        {
            DisposeAudio();
        }
    }

    private void DisposeAudio()
    {
        if (_currentWaveOut != null)
        {
            _currentWaveOut.PlaybackStopped -= OnPlaybackStopped;
            _currentWaveOut.Dispose();
            _currentWaveOut = null;
        }

        _currentReader?.Dispose();
        _currentReader = null;
    }
}
