using System;
using System.Collections.Generic;
using System.Linq;

namespace XorWoWLauncher.Core
{
    public sealed class NoteItem
    {
        public DateTimeOffset Time { get; set; }
        public string When => Time.ToLocalTime().ToString("ddd d MMM yyyy · HH:mm");
        public string Title { get; set; }
        public string Body { get; set; }
        public bool HasBody => !string.IsNullOrEmpty(Body);
        /// <summary>The longer write-up the detail view shows (client\release-details.md, simple markup: "## " headings, "- " bullets, **bold**).</summary>
        public string Details { get; set; }
        public bool HasDetails => !string.IsNullOrEmpty(Details);
        public string Kind { get; set; }   // "Server" or "Addons"
    }

    /// <summary>notes.json: the realm's restarts with their reason, the XorWoW addon releases with their changes, each with its optional details.</summary>
    public static class Notes
    {
        public static (List<NoteItem> server, List<NoteItem> addons) Parse(string json)
        {
            var root = Json.Parse(json);
            var server = Json.Arr(root, "server").Select(o => new NoteItem
            {
                Time = Time(Json.Str(o, "time")),
                Title = Json.Str(o, "text"),
                Details = Json.Str(o, "details"),
                Kind = "Server",
            }).OrderByDescending(n => n.Time).ToList();

            var addons = Json.Arr(root, "addons").Select(o =>
            {
                var changes = Json.Arr(o, "changes").Select(c => "•  " + Convert.ToString(c)).ToList();
                var headline = Json.Str(o, "headline");
                if (!string.IsNullOrEmpty(headline)) changes.Insert(0, headline);
                return new NoteItem
                {
                    Time = Time(Json.Str(o, "time")),
                    Title = "XorWoW addons " + Json.Str(o, "version"),
                    Body = string.Join("\n", changes),
                    Details = Json.Str(o, "details"),
                    Kind = "Addons",
                };
            }).OrderByDescending(n => n.Time).ToList();
            return (server, addons);
        }

        static DateTimeOffset Time(string s) => DateTimeOffset.TryParse(s, out var t) ? t : DateTimeOffset.MinValue;
    }
}
