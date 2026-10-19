using EndpointAgent.Core.ScreenLock;

namespace EndpointAgent.Core.Tests.ScreenLock;

/// <summary>
/// The overlay's pure decisions: which monitors to cover and which shows the
/// content, how to read the service's verify answer, and which keys the input
/// hooks swallow. All testable without a display, a pipe or a hook.
/// </summary>
public sealed class LockScreenViewTests
{
    // --------------------------------------------------------------- monitors

    [Fact]
    public void One_overlay_per_monitor_covers_each_edge_to_edge()
    {
        var monitors = new List<MonitorRect>
        {
            new(0, 0, 1920, 1080, Primary: true),
            new(1920, 0, 2560, 1440, Primary: false),
        };

        var overlays = LockOverlayLayout.For(monitors);

        overlays.Count.ShouldBe(2);
        overlays.Select(o => o.Bounds).ShouldBe(monitors);
    }

    [Fact]
    public void Exactly_one_overlay_carries_the_content_and_it_is_the_primary()
    {
        var monitors = new List<MonitorRect>
        {
            new(-1920, 0, 1920, 1080, Primary: false),
            new(0, 0, 2560, 1440, Primary: true),
            new(2560, 0, 1920, 1080, Primary: false),
        };

        var overlays = LockOverlayLayout.For(monitors);

        overlays.Count(o => o.ShowsContent).ShouldBe(1, "exactly one overlay shows the logo and text");
        overlays.Single(o => o.ShowsContent).Bounds.ShouldBe(monitors[1], "the primary monitor carries the content");
    }

    [Fact]
    public void With_no_primary_marked_the_first_monitor_carries_the_content()
    {
        var monitors = new List<MonitorRect>
        {
            new(0, 0, 1024, 768, Primary: false),
            new(1024, 0, 1024, 768, Primary: false),
        };

        LockOverlayLayout.For(monitors).Single(o => o.ShowsContent).Bounds.ShouldBe(monitors[0]);
    }

    [Fact]
    public void With_no_monitors_reported_a_single_content_overlay_is_still_returned()
    {
        // A lock must never be a silent no-op that leaves the desktop live.
        var overlays = LockOverlayLayout.For([]);

        overlays.ShouldHaveSingleItem().ShowsContent.ShouldBeTrue();
    }

    // ------------------------------------------------------------- the prompt

    [Theory]
    [InlineData(true, LockPromptOutcome.Dismiss)]
    [InlineData(false, LockPromptOutcome.ShowIncorrect)]
    [InlineData(null, LockPromptOutcome.ShowUnavailable)]
    public void The_verify_answer_only_dismisses_on_a_clear_true(bool? unlocked, LockPromptOutcome expected)
    {
        // The fail-safe: an unreachable service (null) never unlocks.
        LockScreenPrompt.Interpret(unlocked).ShouldBe(expected);
    }

    // --------------------------------------------------------- the input hooks

    [Fact]
    public void While_locked_every_key_is_swallowed_except_the_unlock_chord()
    {
        // Ctrl+Alt+U, on key-down, opens the prompt.
        LockInputFilter.DecideKey(prompting: false, ctrlDown: true, altDown: true, virtualKey: LockInputFilter.VkU, keyDown: true)
            .ShouldBe(LockKeyAction.OpenPrompt);

        // The same keys without both modifiers, or on key-up, are just swallowed.
        LockInputFilter.DecideKey(false, ctrlDown: true, altDown: false, LockInputFilter.VkU, keyDown: true).ShouldBe(LockKeyAction.Swallow);
        LockInputFilter.DecideKey(false, ctrlDown: true, altDown: true, LockInputFilter.VkU, keyDown: false).ShouldBe(LockKeyAction.Swallow);

        // Everything else: swallowed.
        LockInputFilter.DecideKey(false, false, false, 0x41, keyDown: true).ShouldBe(LockKeyAction.Swallow); // 'A'
        LockInputFilter.DecideKey(false, ctrlDown: true, altDown: true, 0x09, keyDown: true).ShouldBe(LockKeyAction.Swallow); // Ctrl+Alt+Tab
        LockInputFilter.DecideKey(false, false, false, 0x5B, keyDown: true).ShouldBe(LockKeyAction.Swallow); // LWin
    }

    [Fact]
    public void While_prompting_the_passphrase_keys_pass_and_shell_chords_do_not()
    {
        // Letters, digits and editing keys reach the box.
        LockInputFilter.DecideKey(prompting: true, false, false, 0x41, keyDown: true).ShouldBe(LockKeyAction.PassToPrompt); // 'A'
        LockInputFilter.DecideKey(true, false, false, 0x39, keyDown: true).ShouldBe(LockKeyAction.PassToPrompt); // '9'
        LockInputFilter.DecideKey(true, false, false, 0x08, keyDown: true).ShouldBe(LockKeyAction.PassToPrompt); // Backspace
        LockInputFilter.DecideKey(true, false, false, 0x0D, keyDown: true).ShouldBe(LockKeyAction.PassToPrompt); // Enter
        LockInputFilter.DecideKey(true, false, false, 0x1B, keyDown: true).ShouldBe(LockKeyAction.PassToPrompt); // Escape

        // Shell chords are still swallowed so the prompt cannot be an escape hatch.
        LockInputFilter.DecideKey(true, false, false, 0x09, keyDown: true).ShouldBe(LockKeyAction.Swallow); // Tab
        LockInputFilter.DecideKey(true, false, false, 0x5B, keyDown: true).ShouldBe(LockKeyAction.Swallow); // LWin
        LockInputFilter.DecideKey(true, false, false, 0x70, keyDown: true).ShouldBe(LockKeyAction.Swallow); // F1

        // There is no second way to open the prompt once it is open.
        LockInputFilter.DecideKey(true, ctrlDown: true, altDown: true, LockInputFilter.VkU, keyDown: true).ShouldBe(LockKeyAction.PassToPrompt);
    }

    // ----------------------------------------------------- modifier tracking

    [Fact]
    public void The_chord_fires_from_tracked_modifiers_even_though_the_hook_swallowed_them()
    {
        // The 1.19.0 failure: Ctrl and Alt were swallowed, so the system never saw
        // them held and the chord was dead. The tracker sees the raw events instead.
        var modifiers = new LockModifierState();

        modifiers.Observe(LockInputFilter.VkLControl, keyDown: true).ShouldBeTrue();
        modifiers.Observe(LockInputFilter.VkLMenu, keyDown: true).ShouldBeTrue();
        modifiers.Observe(LockInputFilter.VkU, keyDown: true).ShouldBeFalse("U is not a modifier");

        LockInputFilter.DecideKey(prompting: false, modifiers.CtrlDown, modifiers.AltDown, LockInputFilter.VkU, keyDown: true)
            .ShouldBe(LockKeyAction.OpenPrompt);
    }

    [Fact]
    public void Releasing_a_modifier_clears_it_and_either_side_counts()
    {
        var modifiers = new LockModifierState();

        modifiers.Observe(LockInputFilter.VkRControl, keyDown: true);
        modifiers.Observe(LockInputFilter.VkRMenu, keyDown: true); // AltGr-style right Alt
        modifiers.CtrlDown.ShouldBeTrue();
        modifiers.AltDown.ShouldBeTrue();

        modifiers.Observe(LockInputFilter.VkRControl, keyDown: false);
        modifiers.CtrlDown.ShouldBeFalse();
        modifiers.AltDown.ShouldBeTrue("Alt is still held");

        modifiers.Observe(LockInputFilter.VkRMenu, keyDown: false);
        modifiers.AltDown.ShouldBeFalse();
    }

    [Fact]
    public void Releasing_one_ctrl_does_not_forget_the_other_still_held()
    {
        var modifiers = new LockModifierState();

        modifiers.Observe(LockInputFilter.VkLControl, keyDown: true);
        modifiers.Observe(LockInputFilter.VkRControl, keyDown: true);
        modifiers.Observe(LockInputFilter.VkLControl, keyDown: false);

        modifiers.CtrlDown.ShouldBeTrue();
    }

    [Fact]
    public void The_generic_modifier_codes_are_accepted_too()
    {
        var modifiers = new LockModifierState();

        modifiers.Observe(LockInputFilter.VkControl, keyDown: true);
        modifiers.Observe(LockInputFilter.VkMenu, keyDown: true);

        modifiers.CtrlDown.ShouldBeTrue();
        modifiers.AltDown.ShouldBeTrue();
        LockInputFilter.IsControlKey(LockInputFilter.VkControl).ShouldBeTrue();
        LockInputFilter.IsAltKey(LockInputFilter.VkRMenu).ShouldBeTrue();
        LockInputFilter.IsControlKey(LockInputFilter.VkU).ShouldBeFalse();
    }

    [Fact]
    public void The_mouse_is_always_swallowed()
    {
        LockInputFilter.SwallowMouse().ShouldBeTrue();
    }
}
