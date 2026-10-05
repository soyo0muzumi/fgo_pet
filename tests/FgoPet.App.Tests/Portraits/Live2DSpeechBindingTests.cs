using System.Windows.Threading;
using FgoPet.App.Composition;
using FgoPet.App.Runtime;
using Xunit;

namespace FgoPet.App.Tests.Portraits;

public sealed class Live2DSpeechBindingTests
{
    [Fact]
    public void Speech_frames_keep_the_playback_role_and_reject_old_sessions_and_disposal()
    {
        StaTest.Run(() =>
        {
            var identity = new AppRuntime();
            var levels = new List<double?>();
            using var binding = new Live2DSpeechBinding(identity, levels.Add, _ => true);
            Assert.Null(binding.CreateObserver());
            identity.SetActiveRole(new ActiveRoleState("pkg", "casual", "1", "mash"));
            Drain(); levels.Clear();
            var first = binding.CreateObserver()!;
            first(.8); Drain(); Assert.Equal(.8, Assert.Single(levels));
            var second = binding.CreateObserver()!;
            first(null); second(.5); Drain(); Assert.Equal(.5, levels[^1]);
            Assert.Equal(2, levels.Count);
            second(.9);
            identity.SetActiveRole(new ActiveRoleState("pkg", "casual", "1", "mash"));
            Drain(); Assert.Null(levels[^1]); Assert.DoesNotContain(.9, levels);
            levels.Clear();
            var current = binding.CreateObserver()!;
            current(.7); binding.Dispose(); Drain();
            Assert.DoesNotContain(.7, levels);
        });
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
