using System.Text;
using System.Text.Json;
using TableForge.Domain;
using TableForge.Streaming;

namespace TableForge.Tests;

/// <summary>RC27 Streaming Overlay, phase 1: the overlay-only output limits.</summary>
public class StreamingOverlayLimitsTests
{
    private static OverlayLine Line(string text, string heading = "") => new(heading, [new OverlaySegment(text)]);
    private static OverlayResult Result(params OverlayLine[] lines) => new("Weather", "14", lines);
    private static OverlayResult Named(string name) => new(name, "14", [Line("Clear")]);
    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
            if (char.IsSurrogate(s[i])) return true;
        }
        return false;
    }

    [Fact]
    public void A_result_within_every_limit_is_unchanged_and_not_marked_truncated()
    {
        var source = new OverlayResult("Weather", "14", [Line("Storm brewing", "Sky"), new OverlayLine("", [new("Bold", Bold: true), new(" plain")])]);
        var (limited, truncated) = OverlayLimits.Apply(source);
        Assert.False(truncated);
        Assert.Equal(source.TableName, limited.TableName);
        Assert.Equal(source.Lines.Select(l => (l.Heading, string.Join("|", l.Segments))), limited.Lines.Select(l => (l.Heading, string.Join("|", l.Segments))));
    }

    [Fact]
    public void The_table_name_is_cut_to_200_characters_ending_in_an_ellipsis()
    {
        Assert.False(OverlayLimits.Apply(Named(new string('a', 200))).Truncated);
        var (limited, truncated) = OverlayLimits.Apply(Named(new string('a', 201)));
        Assert.True(truncated);
        Assert.Equal(200, limited.TableName.Length);
        Assert.EndsWith("…", limited.TableName);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var name = new string('a', 198) + "😀" + "b";                                   // 201 UTF-16 characters, the emoji at 198-199
        var cut = OverlayLimits.Apply(Named(name)).Result.TableName;
        Assert.False(HasLoneSurrogate(cut));
        Assert.Equal(new string('a', 198) + "…", cut);

        var text = new string('x', 3998) + "🎲🎲";                                       // 4,002: the cut falls inside the first die
        var line = Assert.Single(OverlayLimits.Apply(Result(Line(text))).Result.Lines);
        Assert.False(HasLoneSurrogate(line.Text));
        Assert.Equal(new string('x', 3998) + "…", line.Text);
    }

    [Fact]
    public void Headings_are_cut_to_100_characters()
    {
        Assert.False(OverlayLimits.Apply(Result(Line("x", new string('h', 100)))).Truncated);
        var (limited, truncated) = OverlayLimits.Apply(Result(Line("x", new string('h', 101))));
        Assert.True(truncated);
        Assert.Equal(new string('h', 99) + "…", limited.Lines[0].Heading);
    }

    [Fact]
    public void At_most_20_lines_and_the_last_one_kept_says_more_followed()
    {
        var twenty = Enumerable.Range(1, 20).Select(i => Line($"Set {i}")).ToArray();
        Assert.False(OverlayLimits.Apply(Result(twenty)).Truncated);

        var (limited, truncated) = OverlayLimits.Apply(Result([.. twenty, Line("Set 21")]));
        Assert.True(truncated);
        Assert.Equal(20, limited.Lines.Count);
        Assert.Equal("Set 20 …", limited.Lines[^1].Text);
        Assert.DoesNotContain(limited.Lines, l => l.Text.Contains("Set 21"));
    }

    [Fact]
    public void The_result_text_is_cut_at_4000_characters_across_lines_in_reading_order()
    {
        Assert.False(OverlayLimits.Apply(Result(Line(new string('a', 4000)))).Truncated);

        var single = OverlayLimits.Apply(Result(Line(new string('a', 4001))));
        Assert.True(single.Truncated);
        Assert.Equal(4000, single.Result.Lines[0].Text.Length);
        Assert.EndsWith("…", single.Result.Lines[0].Text);

        var (limited, truncated) = OverlayLimits.Apply(Result(Line(new string('a', 3000)), Line(new string('b', 1500)), Line("third")));
        Assert.True(truncated);
        Assert.Equal(2, limited.Lines.Count);                                              // the third line had no room left
        Assert.Equal(1000, limited.Lines[1].Text.Length);
        Assert.Equal(4000, limited.Lines.Sum(l => l.Text.Length));
    }

    [Fact]
    public void Text_that_fills_the_budget_exactly_is_not_truncated_and_empty_lines_after_it_stay()
    {
        var (limited, truncated) = OverlayLimits.Apply(Result(Line(new string('a', 4000)), new OverlayLine("Empty", [])));
        Assert.False(truncated);
        Assert.Equal(2, limited.Lines.Count);
    }

    [Fact]
    public void A_cut_segment_keeps_its_bold_and_italic()
    {
        var source = Result(new OverlayLine("", [new OverlaySegment(new string('b', 4100), Bold: true, Italic: true)]));
        var segment = Assert.Single(OverlayLimits.Apply(source).Result.Lines[0].Segments);
        Assert.True(segment.Bold && segment.Italic);
        Assert.Equal(4000, segment.Text.Length);
    }

    [Fact]
    public void At_most_500_segments_the_rest_of_the_line_becoming_one_plain_segment()
    {
        static OverlayLine Many(int count) => new("", Enumerable.Range(0, count).Select(i => new OverlaySegment($"{i % 10}", Bold: i % 2 == 0)).ToList());

        Assert.False(OverlayLimits.Apply(Result(Many(500))).Truncated);

        var source = Result(Many(501));
        var (limited, truncated) = OverlayLimits.Apply(source);
        Assert.True(truncated);
        var segments = limited.Lines[0].Segments;
        Assert.Equal(500, segments.Count);
        Assert.Equal(source.Lines[0].Text, limited.Lines[0].Text);                         // no text lost
        Assert.False(segments[^1].Bold || segments[^1].Italic);
        Assert.Equal("90", segments[^1].Text);                                              // segments 499 and 500 merged
    }

    [Fact]
    public void The_segment_limit_keeps_room_for_every_later_line()
    {
        static OverlayLine Many(int count) => new("", Enumerable.Range(0, count).Select(i => new OverlaySegment("s", Italic: i % 2 == 0)).ToList());
        var (limited, truncated) = OverlayLimits.Apply(Result(Many(600), Line("two"), Line("three"), Line("four")));

        Assert.True(truncated);
        Assert.Equal(500, limited.Lines.Sum(l => l.Segments.Count));
        Assert.Equal(497, limited.Lines[0].Segments.Count);
        Assert.Equal(["two", "three", "four"], limited.Lines.Skip(1).Select(l => l.Text).ToArray());
        Assert.Equal(600, limited.Lines[0].Text.Length);
    }

    [Fact]
    public void The_source_result_is_never_changed()
    {
        var segments = Enumerable.Range(0, 600).Select(i => new OverlaySegment("ab", Bold: true)).ToList();
        var lines = Enumerable.Range(0, 25).Select(i => new OverlayLine(new string('h', 150), i == 0 ? segments : [new OverlaySegment(new string('t', 300))])).ToList();
        var source = new OverlayResult(new string('n', 300), "14", lines);
        var before = JsonSerializer.Serialize(source);

        Assert.True(OverlayLimits.Apply(source).Truncated);

        Assert.Equal(before, JsonSerializer.Serialize(source));
        Assert.Equal(600, segments.Count);
        Assert.Equal(25, lines.Count);
    }
}

/// <summary>RC27 Streaming Overlay, phase 1: the current result, its identity, Test and Clear, and the /state snapshot.</summary>
public class StreamingOverlayPublisherTests
{
    private static OverlayResult R(string table, string value, string text) => new(table, value, [new OverlayLine("", [new OverlaySegment(text)])]);
    private static string Shown(OverlayPublisher p) => p.State.Result?.Lines[0].Text ?? "(empty)";

    [Fact]
    public void A_new_overlay_starts_empty_with_a_random_instance_per_run()
    {
        var p = new OverlayPublisher();
        Assert.True(p.State.IsEmpty);
        Assert.Equal(0, p.State.Version);
        Assert.Matches("^[0-9a-f]{32}$", p.InstanceId);
        Assert.NotEqual(p.InstanceId, new OverlayPublisher().InstanceId);
        Assert.True(p.State.ShowTableName && p.State.ShowRollValue);
    }

    [Fact]
    public void The_latest_result_wins_and_an_inline_roll_on_an_older_one_cannot_replace_it()
    {
        var p = new OverlayPublisher();
        var a = p.Publish(R("Encounters", "3", "You meet 2d6 skeletons."));
        var b = p.Publish(R("Encounters", "9", "You find 1d4 coins."));
        Assert.Equal("You find 1d4 coins.", Shown(p));

        var version = p.State.Version;
        Assert.False(p.UpdateIfCurrent(a, R("Encounters", "3", "You meet 7 skeletons.")));
        Assert.Equal("You find 1d4 coins.", Shown(p));
        Assert.Equal(version, p.State.Version);

        Assert.True(p.UpdateIfCurrent(b, R("Encounters", "9", "You find 3 coins.")));
        Assert.Equal("You find 3 coins.", Shown(p));
        Assert.Equal(version + 1, p.State.Version);
        Assert.Equal(b.Generation, p.State.Generation);                                    // still the same result
        Assert.True(p.UpdateIfCurrent(b, R("Encounters", "9", "You find 4 coins.")));      // and it can be rolled again
    }

    [Fact]
    public void Clear_empties_the_overlay_and_no_earlier_inline_roll_can_bring_a_result_back()
    {
        var p = new OverlayPublisher();
        var a = p.Publish(R("T", "1", "One 1d6"));
        p.Clear();
        Assert.True(p.State.IsEmpty);
        Assert.False(p.UpdateIfCurrent(a, R("T", "1", "One 4")));
        Assert.True(p.State.IsEmpty);
    }

    [Fact]
    public void Show_test_result_shows_the_test_and_makes_earlier_results_stale()
    {
        var p = new OverlayPublisher();
        var a = p.Publish(R("T", "1", "One 1d6"));
        p.ShowTest();
        Assert.Equal(("TableForge Overlay Test", "12", "Your streaming overlay is working."),
            (p.State.Result!.TableName, p.State.Result.RollValue, Shown(p)));
        Assert.False(p.UpdateIfCurrent(a, R("T", "1", "One 4")));
        Assert.Equal("Your streaming overlay is working.", Shown(p));
        p.Clear();
        Assert.True(p.State.IsEmpty);
    }

    [Fact]
    public void Show_table_name_and_roll_value_change_only_how_the_current_result_is_shown()
    {
        var p = new OverlayPublisher();
        var a = p.Publish(R("T", "1", "One 1d6"));
        var version = p.State.Version;

        p.SetPresentation(showTableName: false, showRollValue: true);
        Assert.Equal((false, true, version + 1, a.Generation), (p.State.ShowTableName, p.State.ShowRollValue, p.State.Version, p.State.Generation));
        p.SetPresentation(showTableName: false, showRollValue: true);                       // no change, no new version
        Assert.Equal(version + 1, p.State.Version);
        Assert.True(p.UpdateIfCurrent(a, R("T", "1", "One 4")));                            // the result is still current
    }

    [Fact]
    public void Every_change_advances_the_version_and_raises_changed()
    {
        var p = new OverlayPublisher();
        var versions = new List<long>();
        p.Changed += (_, _) => versions.Add(p.State.Version);
        var a = p.Publish(R("T", "1", "a"));
        p.UpdateIfCurrent(a, R("T", "1", "b"));
        p.UpdateIfCurrent(new OverlayToken(999), R("T", "1", "ignored"));
        p.SetPresentation(false, false);
        p.ShowTest();
        p.Clear();
        Assert.Equal([1L, 2, 3, 4, 5], versions.ToArray());
    }

    [Fact]
    public void The_state_json_carries_the_instance_version_flags_and_lines()
    {
        var p = new OverlayPublisher(instanceId: "abc");
        using (var empty = JsonDocument.Parse(p.StateJson))
        {
            var root = empty.RootElement;
            Assert.Equal(("abc", 0, true), (root.GetProperty("instance").GetString(), root.GetProperty("version").GetInt64(), root.GetProperty("empty").GetBoolean()));
            Assert.False(root.TryGetProperty("lines", out _));
        }

        p.Publish(new OverlayResult("Weather", "00", [new OverlayLine("Sky", [new("Storm ", Bold: true), new("brewing", Italic: true)])]));
        using var doc = JsonDocument.Parse(p.StateJson);
        var r = doc.RootElement;
        Assert.Equal(("Weather", "00", false, false), (r.GetProperty("tableName").GetString(), r.GetProperty("rollValue").GetString(),
            r.GetProperty("empty").GetBoolean(), r.GetProperty("truncated").GetBoolean()));
        var line = r.GetProperty("lines")[0];
        Assert.Equal("Sky", line.GetProperty("heading").GetString());
        var segs = line.GetProperty("segments");
        Assert.True(segs[0].GetProperty("bold").GetBoolean());
        Assert.False(segs[0].TryGetProperty("italic", out _));
        Assert.True(segs[1].GetProperty("italic").GetBoolean());
    }

    [Fact]
    public void Markup_and_unicode_in_table_text_are_only_ever_escaped_data()
    {
        var p = new OverlayPublisher();
        const string text = "<script>alert('x')</script> & \"quoted\" 🎲 雨の日には";
        p.Publish(R("<b>Name</b>", "1", text));
        var raw = Encoding.UTF8.GetString(p.StateJson);
        Assert.DoesNotContain("<script>", raw);
        Assert.DoesNotContain("<b>", raw);
        using var doc = JsonDocument.Parse(p.StateJson);
        Assert.Equal(text, doc.RootElement.GetProperty("lines")[0].GetProperty("segments")[0].GetProperty("text").GetString());
        Assert.Equal("<b>Name</b>", doc.RootElement.GetProperty("tableName").GetString());
    }

    [Fact]
    public void The_state_is_bounded_and_published_through_the_limits()
    {
        var p = new OverlayPublisher();
        p.Publish(R(new string('n', 500), "1", new string('t', 9000)));
        Assert.True(p.State.Truncated);
        Assert.Equal(200, p.State.Result!.TableName.Length);
        Assert.Equal(4000, p.State.Result.Lines[0].Text.Length);
        using var doc = JsonDocument.Parse(p.StateJson);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void The_largest_result_the_limits_allow_still_fits_the_64_KiB_guard()
    {
        // Worst case for size: every limit filled, every character one the encoder must escape (\uXXXX, 6 bytes each).
        var lines = Enumerable.Range(0, OverlayLimits.MaxLines).Select(i => new OverlayLine(new string('<', OverlayLimits.MaxHeadingLength),
            Enumerable.Range(0, OverlayLimits.MaxSegments / OverlayLimits.MaxLines)
                .Select(_ => new OverlaySegment(new string('é', OverlayLimits.MaxTextLength / OverlayLimits.MaxSegments), true, true)).ToList())).ToList();
        var p = new OverlayPublisher();
        p.Publish(new OverlayResult(new string('&', OverlayLimits.MaxTableNameLength), "-2147483648", lines));

        Assert.False(p.State.IsEmpty);
        Assert.Equal(0, p.OversizedCount);
        Assert.InRange(p.StateJson.Length, 40_000, OverlayPublisher.DefaultMaxStateBytes);
    }

    [Fact]
    public void A_state_over_the_guard_is_served_as_an_empty_overlay_and_counted()
    {
        var p = new OverlayPublisher(maxStateBytes: 400);
        var big = R("T", "1", new string('x', 1000));
        var a = p.Publish(big);

        Assert.True(p.State.IsEmpty);
        Assert.Equal(1, p.OversizedCount);
        Assert.True(p.StateJson.Length <= 400);
        Assert.Equal(1000, big.Lines[0].Text.Length);                                      // the source is untouched
        Assert.False(p.UpdateIfCurrent(a, R("T", "1", "small")));                         // nothing to update in place

        p.Publish(R("T", "2", "small"));
        Assert.False(p.State.IsEmpty);
    }

    [Fact]
    public void Formatted_segments_come_straight_from_the_rich_text_model()
    {
        var segment = OverlaySegment.From(new FormattedSegment("Heavy", TextStyle.Bold | TextStyle.Italic));
        Assert.Equal(new OverlaySegment("Heavy", true, true), segment);
        Assert.Equal(new OverlaySegment("plain"), OverlaySegment.From(new FormattedSegment("plain", TextStyle.None)));
    }
}
