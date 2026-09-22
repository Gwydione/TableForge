using Xunit;

// The "UI" collection's tests drive real WPF windows and read the machine's real keyboard-modifier state
// (Keyboard.Modifiers), which is sensitive to timing. Running other collections in parallel alongside it was
// observed to make those tests intermittently fail under CPU contention, even though they are already serialized
// against each other. Since this suite gates the release publish script, determinism matters more than the
// suite's wall-clock time here.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
