using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using NetLights.App;
using NetLights.Core;
using NetLights.Updates;
using Xunit;

namespace NetLights.IntegrationTests;

[Collection("SettingsFiles")]
public sealed class UiRendererTests
{
    [Fact]
    public void T45_SixteenCombinationsRenderTransparentGap()
    {
        GroupAvailability[] states =
        [
            GroupAvailability.Online,
            GroupAvailability.Limited,
            GroupAvailability.Offline,
            GroupAvailability.Unknown
        ];
        string dir = Path.Combine(Path.GetTempPath(), "net-lights-icons");
        Directory.CreateDirectory(dir);
        foreach (GroupAvailability left in states)
        {
            foreach (GroupAvailability right in states)
            {
                using Bitmap bmp16 = TrayIconRenderer.RenderBitmap(left, right, 16);
                using Bitmap bmp32 = TrayIconRenderer.RenderBitmap(left, right, 32);
                Assert.Equal(16, bmp16.Width);
                Color center16 = bmp16.GetPixel(8, 8);
                Color center32 = bmp32.GetPixel(16, 16);
                Assert.True(center16.A < 40, $"16 gap alpha {center16.A}");
                Assert.True(center32.A < 40, $"32 gap alpha {center32.A}");
                bmp32.Save(Path.Combine(dir, $"{left}-{right}.png"));
            }
        }
    }

    [Fact]
    public void T53_RejectsBadEndpointFileWithoutThrowing()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            File.WriteAllText(
                SettingsStore.EndpointsPath,
                """[{"id":"x","group":"mars","uri":"https://example.test/","infrastructureId":"x"}]""");
            (MonitorConfiguration config, string? warning) = SettingsStore.LoadPool();
            Assert.NotNull(warning);
            Assert.True(config.UsingBuiltinPool);
            Assert.Equal(14, config.Endpoints.Count);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void AgeLabel_CountsWholeSecondsAndNeverGoesNegative()
    {
        DateTimeOffset now = new(2026, 9, 7, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal("", StatusForm.AgeLabel(null, now));
        Assert.Equal("0 сек назад", StatusForm.AgeLabel(now, now));
        Assert.Equal("1 сек назад", StatusForm.AgeLabel(now.AddMilliseconds(-1499), now));
        Assert.Equal("12 сек назад", StatusForm.AgeLabel(now.AddSeconds(-12), now));
        Assert.Equal("0 сек назад", StatusForm.AgeLabel(now.AddSeconds(3), now));
    }

    [Fact]
    public void RowStyle_UsesCoolBlueForRuAndWarmAmberForWorld()
    {
        (Color ruOkBack, Color ruOkFore) = StatusForm.RowStyle(EndpointGroup.Ru, ProbeOutcome.Reachable);
        (Color ruDownBack, _) = StatusForm.RowStyle(EndpointGroup.Ru, ProbeOutcome.Unreachable);
        (Color worldOkBack, _) = StatusForm.RowStyle(EndpointGroup.World, ProbeOutcome.Reachable);
        (Color worldDownBack, _) = StatusForm.RowStyle(EndpointGroup.World, ProbeOutcome.Unreachable);
        Assert.True(ruOkBack.B > ruOkBack.R && ruOkBack.B > ruOkBack.G);
        Assert.True(ruDownBack.GetBrightness() < ruOkBack.GetBrightness());
        Assert.True(worldOkBack.R > worldOkBack.B && worldOkBack.G > worldOkBack.B);
        Assert.True(worldDownBack.GetBrightness() < worldOkBack.GetBrightness());
        Assert.True(ruOkFore.GetBrightness() < 0.45f);
    }

    [Fact]
    public void StatusForm_ClockTicksDoNotBeginUpdate()
    {
        using var form = new StatusForm
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000)
        };
        form.Reveal();
        var empty = new MonitorKernel(new MonitorConfiguration { Endpoints = BuiltinEndpoints.All }, TimeProvider.System).Snapshot;
        form.Bind(empty);
        int binds = form.DataBindCount;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            form.RefreshAges();
        }

        sw.Stop();
        Assert.Equal(binds, form.DataBindCount);
        Assert.Equal(14, StatusSnapshotProjector.Rows(empty).Count);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"1000 clock ticks took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void StatusForm_UserCloseHidesWithoutRecreatingTaskbarWindow()
    {
        using var form = new StatusForm();
        form.Reveal();
        Assert.True(form.Visible);
        Assert.True(form.ShowInTaskbar);
        Assert.True(form.HideToTrayIfUserClosing(CloseReason.UserClosing));
        Assert.False(form.Visible);
        Assert.True(form.ShowInTaskbar);
        Assert.False(form.HideToTrayIfUserClosing(CloseReason.ApplicationExitCall));
    }

    [Fact]
    public void StatusForm_HeaderHeightStaysStableAfterHideAndReveal()
    {
        using var form = new StatusForm
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000)
        };
        form.Reveal();
        form.PerformLayout();
        Control header = Find<Control>(form, "windowHeader")!;
        int first = header.Height;
        Assert.True(first >= 72, "header " + first);
        Assert.True(form.HideToTrayIfUserClosing(CloseReason.UserClosing));
        form.Reveal();
        form.PerformLayout();
        int second = header.Height;
        Assert.InRange(second, first - 8, first + 8);
    }

    [Fact]
    public void StatusForm_DefaultSizeIsTallerThanTheCompactMinimum()
    {
        using var form = new StatusForm();
        Assert.Equal(UiTheme.WindowMinWidth, form.MinimumSize.Width);
        Assert.Equal(UiTheme.WindowMinHeight, form.MinimumSize.Height);
        Assert.True(UiTheme.WindowDefaultHeight > UiTheme.WindowMinHeight);
        form.PlaceCentered();
        Rectangle area = Screen.FromPoint(form.Location).WorkingArea;
        int expectedHeight = Math.Min(Math.Max(form.MinimumSize.Height, UiTheme.WindowDefaultHeight), Math.Max(240, area.Height));
        if (expectedHeight > area.Height)
        {
            expectedHeight = area.Height;
        }

        int expectedWidth = Math.Min(Math.Max(form.MinimumSize.Width, UiTheme.WindowDefaultWidth), Math.Max(320, area.Width));
        if (expectedWidth > area.Width)
        {
            expectedWidth = area.Width;
        }

        Assert.Equal(Math.Max(240, expectedHeight), form.Height);
        Assert.Equal(Math.Max(320, expectedWidth), form.Width);
    }

    [Fact]
    public void PausedIcon_UsesCoolBlueNotUnknownGray()
    {
        using Bitmap paused = TrayIconRenderer.RenderBitmap(GroupAvailability.Online, GroupAvailability.Online, 32, paused: true);
        using Bitmap unknown = TrayIconRenderer.RenderBitmap(GroupAvailability.Unknown, GroupAvailability.Unknown, 32);
        Color pausePixel = paused.GetPixel(8, 16);
        Color unknownPixel = unknown.GetPixel(8, 16);
        Assert.True(pausePixel.B > pausePixel.R + 20 && pausePixel.B > pausePixel.G, $"pause {pausePixel}");
        Assert.NotEqual(unknownPixel, pausePixel);
        Color gap = paused.GetPixel(16, 16);
        Assert.True(gap.A < 40, $"gap alpha {gap.A}");
    }

    [Fact]
    public void Settings_CheckUpdatesAndExportButtonsShareHeightAndBaseline()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        FindButton(form, "settingsTab").PerformClick();
        ThemedButton check = FindButton(form, "checkUpdates");
        ThemedButton export = FindButton(form, "exportLog");
        check.FitToText();
        export.FitToText();
        Assert.Equal("Проверить обновления", check.Text);
        Assert.Equal("Экспорт журнала", export.Text);
        Assert.Equal(check.Height, export.Height);
        Assert.Equal(check.Top, export.Top);
        Size exportText = TextRenderer.MeasureText(
            export.Text,
            export.Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        Assert.True(export.Width >= exportText.Width + 24, "export width " + export.Width + " text " + exportText.Width);
        Assert.Null(export.Region);
        Assert.True(export.Left >= check.Right);
    }

    [Fact]
    public void StatusForm_HasNoNodesTab()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        Assert.Null(FindButtonByText(form, "Узлы"));
        Assert.Equal("Параметры", FindButton(form, "settingsTab").Text);
        Assert.Equal("Локации", FindButton(form, "locationsTab").Text);
        Assert.Equal("Диагностика", FindButtonByText(form, "Диагностика")?.Text);
    }

    [Fact]
    public void Settings_HasSourceLinksAndNoLecture()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        FindButton(form, "settingsTab").PerformClick();
        Assert.Null(Find<Label>(form, "settingsHelp"));
        Assert.DoesNotContain("Крестик прячет", FlattenText(form), StringComparison.Ordinal);
        Assert.Equal("Исходный код", Find<LinkLabel>(form, "githubLink")!.Text);
        Assert.Equal("Releases", Find<LinkLabel>(form, "releasesLink")!.Text);
    }

    [Fact]
    public void StatusForm_LocalMarkIsOnlyOnWindowTitle()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        Assert.Equal("Net Lights", FindLabel(form, "productTitle").Text);
        Assert.Equal(ProductInfo.DisplayName(), form.Text);
        if (form.Text.Contains("локальная", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("локальная", FindLabel(form, "productTitle").Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Settings_CheckUpdatesClickInvokesCallbackAndBusyDisablesButton()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        FindButton(form, "settingsTab").PerformClick();
        int clicks = 0;
        form.CheckUpdatesRequested = () => clicks++;
        ThemedButton check = FindButton(form, "checkUpdates");
        Label status = FindLabel(form, "manualUpdateStatus");
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(check, [EventArgs.Empty]);
        Assert.Equal(1, clicks);
        form.SetManualUpdateState(true, ManualUpdateCopy.Checking);
        Assert.False(check.Enabled);
        Assert.Equal(ManualUpdateCopy.Checking, status.Text);
        form.SetManualUpdateState(false, ManualUpdateCopy.For(SilentUpdateOutcome.NoUpdate));
        Assert.True(check.Enabled);
        Assert.Equal(ManualUpdateCopy.For(SilentUpdateOutcome.NoUpdate), status.Text);
    }

    [Fact]
    public void TooltipAndBadges_UseProviderLabel()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        var snapshot = new MonitorSnapshot(
            1,
            DateTimeOffset.UtcNow,
            1,
            new GroupSnapshot(EndpointGroup.Ru, GroupAvailability.Online, "", null, false, []),
            new GroupSnapshot(EndpointGroup.World, GroupAvailability.Online, "", null, false, []),
            true,
            null,
            false,
            null,
            false);
        form.Bind(snapshot);
        string tooltip = DiagnosticExport.FormatTooltip(snapshot);
        Assert.Contains(GroupLabels.Provider, tooltip, StringComparison.Ordinal);
        Assert.NotNull(FindBadge(form, GroupLabels.Provider));
    }

    private static StatusBadge? FindBadge(Control root, string prefix)
    {
        if (root is StatusBadge badge && badge.Text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return badge;
        }

        foreach (Control child in root.Controls)
        {
            StatusBadge? nested = FindBadge(child, prefix);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static ThemedButton FindButton(Control root, string accessibleName)
    {
        ThemedButton? found = Find<ThemedButton>(root, accessibleName);
        Assert.NotNull(found);
        return found;
    }

    private static Label FindLabel(Control root, string accessibleName)
    {
        Label? found = Find<Label>(root, accessibleName);
        Assert.NotNull(found);
        return found;
    }

    private static ThemedButton? FindButtonByText(Control root, string text)
    {
        if (root is ThemedButton match && match.Text == text)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            ThemedButton? nested = FindButtonByText(child, text);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    [Fact]
    public void SettingsCardsStayInsideClient()
    {
        using var form = Offscreen(new StatusForm());
        form.Reveal();
        FindButton(form, "settingsTab").PerformClick();
        form.PerformLayout();
        VerticalStack stack = Find<VerticalStack>(form, "settingsStack")!;
        Assert.NotNull(stack);
        stack.Relayout();
        Assert.False(stack.HorizontalScroll.Visible);
        Assert.True(stack.Controls.Count >= 2);
        foreach (Control child in stack.Controls)
        {
            Assert.True(child.Right <= stack.ClientSize.Width + 1, child.GetType().Name + " right " + child.Right + " client " + stack.ClientSize.Width);
            Assert.True(child.Left >= 0, child.GetType().Name + " left " + child.Left);
        }
    }

    [Fact]
    public void LiveColumnsFitInsideList()
    {
        using var form = Offscreen(new StatusForm());
        form.Reveal();
        form.PerformLayout();
        ListView list = Find<ListView>(form, "Текущие проверки")!;
        Assert.NotNull(list);
        int sum = list.Columns.Cast<ColumnHeader>().Sum(column => column.Width);
        Assert.True(sum <= list.ClientSize.Width, "columns " + sum + " client " + list.ClientSize.Width);
        Assert.DoesNotContain("Крестик прячет", FlattenText(form), StringComparison.Ordinal);
    }

    private static StatusForm Offscreen(StatusForm form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        return form;
    }

    private static string FlattenText(Control root)
    {
        var parts = new List<string>();
        CollectText(root, parts);
        return string.Join(" ", parts);
    }

    private static void CollectText(Control root, List<string> parts)
    {
        if (!string.IsNullOrWhiteSpace(root.Text))
        {
            parts.Add(root.Text);
        }

        foreach (Control child in root.Controls)
        {
            CollectText(child, parts);
        }
    }

    private static T? Find<T>(Control root, string accessibleName) where T : Control
    {
        if (root is T match && root.AccessibleName == accessibleName)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            T? nested = Find<T>(child, accessibleName);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
