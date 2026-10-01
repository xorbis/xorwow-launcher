using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace XorWoWLauncher.Core
{
    /// <summary>
    /// JSON through the framework's own JavaScriptSerializer (no extra DLL next to the exe). Our own
    /// state files are typed; the manifest and Warperia's answers are read as loose dictionaries
    /// with the accessors below, since Warperia's fields change type from one addon to the next.
    /// </summary>
    public static class Json
    {
        static JavaScriptSerializer New() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

        public static string Serialize(object o) => New().Serialize(o);
        public static T Deserialize<T>(string s) => New().Deserialize<T>(s);
        public static object Parse(string s) => New().DeserializeObject(s);

        public static IDictionary<string, object> Obj(object o, string key) => Get(o, key) as IDictionary<string, object>;

        public static object Get(object o, string key) =>
            o is IDictionary<string, object> d && d.TryGetValue(key, out var v) ? v : null;

        public static string Str(object o, string key)
        {
            var v = Get(o, key);
            if (v == null || v is bool) return "";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long Long(object o, string key)
        {
            var v = Get(o, key);
            if (v == null) return 0;
            if (v is int i) return i;
            if (v is long l) return l;
            if (v is decimal m) return (long)m;
            if (v is double d) return (long)d;
            long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var r);
            return r;
        }

        public static List<object> Arr(object o, string key) => List(Get(o, key));

        public static List<object> List(object v)
        {
            if (v is string || v == null) return new List<object>();
            if (v is IEnumerable e) return e.Cast<object>().ToList();
            return new List<object>();
        }
    }
}
