using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Threading.Tasks;
using Peek.Common.Helpers;
using Windows.Media.Core;
using Windows.Media.Playback;
using static Peek.Common.Helpers.AudioSessionInterfaces;

internal static class Program
{
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Guid MixerContext = Guid.NewGuid();
    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "saved-volume.json");

    public static async Task Main(string[] args)
    {
        string audioPath = Path.Combine(AppContext.BaseDirectory, "silence.wav");
        using (var writer = new BinaryWriter(File.Create(audioPath)))
        {
            writer.Write("RIFF"u8); writer.Write(160036); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(160000); writer.Write(new byte[160000]);
        }

        double volume = args[0] == "save" ? 0.2 : JsonSerializer.Deserialize<double>(File.ReadAllText(SettingsPath));
        int saves = 0;
        var errors = new List<Exception>();
        if (args[0] == "save")
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(volume));
        }

        using var helper = await AudioSessionVolumeHelper.CreateAsync(
            () => volume,
            value => { volume = value; saves++; File.WriteAllText(SettingsPath, JsonSerializer.Serialize(value)); },
            ex => { errors.Add(ex); Console.Error.WriteLine(ex); });
        Check(helper != null, "audio session helper initialized");

        using var player = new MediaPlayer { IsMuted = true, Volume = 0.4 };
        player.Source = MediaSource.CreateFromUri(new Uri(audioPath));
        player.Play();
        await WaitUntil(() => OwnVolumes().Count > 0 && OwnVolumes().TrueForAll(value => Math.Abs(value - volume) < 0.00001));
        Check(saves == 0, "restoring initial volume does not save a default over it");
        Console.WriteLine($"PASS: new audio session restored to {volume:P0}; player gain stays {player.Volume:P0}");

        if (args[0] == "save")
        {
            SetOwnVolume(0.35f);
            await WaitUntil(() => Math.Abs(volume - 0.35) < 0.00001 && saves > 0);
            Check(Math.Abs(player.Volume - 0.4) < 0.00001, "test changed the Windows mixer, not player gain");
            Console.WriteLine("PASS: Windows mixer change persisted to disk");
            using var second = new MediaPlayer { IsMuted = true };
            second.Source = MediaSource.CreateFromUri(new Uri(audioPath));
            second.Play();
            await WaitUntil(() => OwnVolumes().TrueForAll(value => Math.Abs(value - 0.35) < 0.00001));
            Console.WriteLine("PASS: subsequent media player uses the same saved mixer level");
            second.Pause();
        }
        else
        {
            Check(Math.Abs(volume - 0.35) < 0.00001, "volume survived a process restart");
            Console.WriteLine("PASS: saved mixer volume survived a fresh process");
        }

        helper!.Dispose();
        int previousSaves = saves;
        SetOwnVolume(0.75f);
        await Task.Delay(300);
        Check(saves == previousSaves, "disposed helper does not keep saving volume changes");
        Check(errors.Count == 0, "no native audio errors");
        Console.WriteLine("PASS: native notifications released on disposal");
        player.Pause();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Timed out waiting for audio session volume");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Wrap<T>(nint pointer)
    {
        try { return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(pointer); }
    }

    private static List<float> OwnVolumes(float? setVolume = null)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
        try
        {
            var result = new List<float>();
            Marshal.ThrowExceptionForHR(CoCreateInstance(DeviceEnumeratorClassId, 0, 23, typeof(IDeviceEnumerator).GUID, out var pointer));
            var enumerator = Wrap<IDeviceEnumerator>(pointer);
            enumerator.GetDefaultAudioEndpoint(0, 0, out pointer);
            var device = Wrap<IDevice>(pointer);
            device.Activate(typeof(ISessionManager).GUID, 23, 0, out pointer);
            var manager = Wrap<ISessionManager>(pointer);
            manager.GetSessionEnumerator(out pointer);
            var sessions = Wrap<ISessionEnumerator>(pointer);
            sessions.GetCount(out var count);
            for (int i = 0; i < count; i++)
            {
                sessions.GetSession(i, out pointer);
                var session = Wrap<ISessionControl>(pointer);
                session.GetProcessId(out var process);
                if (process == Environment.ProcessId)
                {
                    var audioVolume = (ISimpleVolume)session;
                    if (setVolume.HasValue) audioVolume.SetMasterVolume(setVolume.Value, MixerContext);
                    audioVolume.GetMasterVolume(out var value);
                    result.Add(value);
                }
                ((ComObject)(object)session).FinalRelease();
            }
            ((ComObject)(object)sessions).FinalRelease();
            ((ComObject)(object)manager).FinalRelease();
            ((ComObject)(object)device).FinalRelease();
            ((ComObject)(object)enumerator).FinalRelease();
            return result;
        }
        finally { CoUninitialize(); }
    }

    private static void SetOwnVolume(float volume) => OwnVolumes(volume);
}
