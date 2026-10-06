using System.IO;
using System.Text;
using System.Text.Json;

namespace TableForge.Streaming;

/// <summary>
/// The Streaming Overlay page OBS shows. The same page is served at <c>/</c> (it polls <c>/state</c> on its own origin) and
/// written into the data folder as <see cref="FileName"/> for OBS's Browser Source "Local file" option (it polls
/// <c>http://127.0.0.1:{port}/state</c>, which grants read access to OBS's local-file origin only). Loaded from a file, the page
/// is there even while TableForge is not running, and starts showing results by itself as soon as TableForge does.
/// <para>
/// It polls about every 500 ms and re-renders only when the (instance, version) pair changes; after two failed polls in a row
/// (TableForge closed or crashed) it shows nothing. Every value from the state is put in with textContent: no table text is ever
/// turned into markup. Transparent, no animation; the tf-* classes are the only customisation hooks (OBS's Custom CSS).
/// </para>
/// </summary>
public static class OverlayPage
{
    public const string FileName = "streaming-overlay.html";
    public const int PollMilliseconds = 500;
    public const int FailuresBeforeClear = 2;

    /// <summary>The page, polling <paramref name="stateUrl"/>. Plain ASCII.</summary>
    public static string Html(string stateUrl) => Template
        .Replace("__STATE_URL__", JsonSerializer.Serialize(stateUrl))
        .Replace("__POLL_MS__", PollMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("__FAILURES__", FailuresBeforeClear.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The page as served at <c>/</c>: it polls its own origin.</summary>
    public static byte[] Served { get; } = Encoding.UTF8.GetBytes(Html("/state"));

    /// <summary>The address the local-file page polls.</summary>
    public static string StateUrl(int port) => $"http://127.0.0.1:{port}/state";

    /// <summary>
    /// Writes the local-file page for <paramref name="port"/> into <paramref name="dataFolder"/> (a temporary file, then an atomic
    /// replace) and returns its full path. Rewritten whenever the port changes.
    /// </summary>
    public static string WriteLocalFile(string dataFolder, int port)
    {
        Directory.CreateDirectory(dataFolder);
        var path = Path.GetFullPath(Path.Combine(dataFolder, FileName));
        var temp = path + ".tmp";
        File.WriteAllText(temp, Html(StateUrl(port)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>TableForge Streaming Overlay</title>
<style>
  html, body { margin: 0; padding: 0; background: transparent; overflow: hidden; }
  .tf-overlay { box-sizing: border-box; padding: 16px 20px; color: #ffffff;
    font-family: "Segoe UI", system-ui, sans-serif;
    text-shadow: 0 1px 3px rgba(0, 0, 0, 0.9), 0 0 2px rgba(0, 0, 0, 0.9); }
  .tf-overlay:empty { display: none; }
  .tf-table-name { font-size: 22px; font-weight: 600; opacity: 0.9; margin-bottom: 4px; }
  .tf-result { font-size: 34px; line-height: 1.25; }
  .tf-line { margin: 0 0 6px 0; }
  .tf-roll-value { font-weight: 700; }
  .tf-roll-separator { opacity: 0.85; }
  .tf-set-heading { font-weight: 600; opacity: 0.85; }
  .tf-result-text { white-space: pre-line; }
  .tf-bold { font-weight: 700; }
  .tf-italic { font-style: italic; }
</style>
</head>
<body>
<div class="tf-overlay" id="tf-overlay"></div>
<script>
(function () {
  'use strict';
  var STATE_URL = __STATE_URL__;
  var POLL_MS = __POLL_MS__;
  var FAILURES_BEFORE_CLEAR = __FAILURES__;
  var root = document.getElementById('tf-overlay');
  var shown = null;
  var failures = 0;

  function clear() {
    while (root.firstChild) root.removeChild(root.firstChild);
    shown = null;
  }

  // Every value from TableForge goes in as text, never as markup.
  function add(parent, tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = String(text);
    parent.appendChild(node);
    return node;
  }

  function render(state) {
    clear();
    if (state.empty || !state.lines) return;
    if (state.showTableName && state.tableName) add(root, 'div', 'tf-table-name', state.tableName);
    var result = add(root, 'div', 'tf-result');
    for (var i = 0; i < state.lines.length; i++) {
      var line = state.lines[i];
      var row = add(result, 'div', 'tf-line');
      if (i === 0 && state.showRollValue && state.rollValue) {
        add(row, 'span', 'tf-roll-value', state.rollValue);
        add(row, 'span', 'tf-roll-separator', ' \u2014 ');
      }
      if (line.heading) add(row, 'span', 'tf-set-heading', line.heading + ': ');
      var text = add(row, 'span', 'tf-result-text');
      var segments = line.segments || [];
      for (var j = 0; j < segments.length; j++) {
        var segment = segments[j];
        var classes = (segment.bold ? 'tf-bold' : '') + (segment.bold && segment.italic ? ' ' : '') + (segment.italic ? 'tf-italic' : '');
        add(text, 'span', classes, segment.text);
      }
    }
  }

  function poll() {
    fetch(STATE_URL, { cache: 'no-store' })
      .then(function (response) { if (!response.ok) throw new Error('HTTP ' + response.status); return response.json(); })
      .then(function (state) {
        failures = 0;
        var key = state.instance + ':' + state.version;
        if (key !== shown) { render(state); shown = key; }
      })
      .catch(function () {
        failures++;
        if (failures >= FAILURES_BEFORE_CLEAR) clear();
      })
      .then(function () { setTimeout(poll, POLL_MS); });
  }

  poll();
})();
</script>
</body>
</html>
""";
}
