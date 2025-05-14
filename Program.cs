// Program.cs  –  .NET 8 / C# 12
using CsvHelper;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// ────────────────────────── تنظیمات اصلی
const string CSV_FILE = @"d:\1400.csv";
const string API_URL = "https://labsnet.ir/api/add_service";

const int MAX_PER = 2;          // حداکثر خدمات در هر درخواست
const int TIMEOUT = 30;         // ثانیه
const int FLUSH_EVERY = 100;        // هر ۱۰۰ ردیف لاگ/فایل‌ها به‌روزرسانی شود
const int DELAY_MS = 400;        // مکث بین درخواست‌ها (ms)

// ---- Retry
const int MAX_RETRIES = 5;          // چند بار تلاش مجدد؟
const int RETRY_DELAYMS = 1000;      // فاصلهٔ بین تلاش‌ها (ms)

// ---- از این سطر اکسل به بعد ارسال شود (هدر = ردیف 1)
const int START_EXCEL_ROW = 68171;     // ← عدد دلخواهتان را این‌جا بدهید

// ---- احراز هویت ثابت
var AUTH = new Dictionary<string, string>
{
    ["user_name"] = "bonyadrazi",
    ["password"] = "bonyadrazi",
    ["ip"] = "46.209.203.113",
    ["lab_id"] = "10575"
};

// ────────────────────────── کمک‌تابع‌ها
static string DigitsOnly(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return "0";
    var d = Regex.Replace(s, @"[^\d]", "");
    return d.Length == 0 ? "0" : d;
}
static string FirstMobile(string? s) =>
    Regex.Match(s ?? "", @"09\d{9}").Value;

static async Task<HttpResponseMessage> PostWithRetryAsync(
        HttpClient client, string url, IEnumerable<KeyValuePair<string, string>> form)
{
    for (int attempt = 1; ; attempt++)
    {
        try { return await client.PostAsync(url, new FormUrlEncodedContent(form)); }
        catch (HttpRequestException) when (attempt <= MAX_RETRIES)
        { await Task.Delay(RETRY_DELAYMS); }
    }
}

// ────────────────────────── ۱) خواندن و آماده‌سازی CSV
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("Reading CSV …");

List<Row> rows;
using (var rdr = new StreamReader(CSV_FILE, Encoding.UTF8))
using (var csv = new CsvReader(rdr, CultureInfo.InvariantCulture))
    rows = csv.GetRecords<Row>().ToList();

/* حذف ردیف‌های قبل از START_EXCEL_ROW */
int skipCount = Math.Max(0, START_EXCEL_ROW - 2);  // چون دادهٔ واقعی از ردیف 2 شروع می‌شود
if (skipCount > 0)
    rows = rows.Skip(skipCount).ToList();

/* شماره‌گذاری دقیقِ ردیف‌های باقی‌مانده */
for (int i = 0; i < rows.Count; i++)
    rows[i].excel_row = i + START_EXCEL_ROW;       // حالا ردیف اکسل صحیح است

/* پاک‌سازی مقادیر */
foreach (var r in rows)
{
    r.price = DigitsOnly(r.price);
    r.discount = DigitsOnly(r.discount);
    r.test_count = DigitsOnly(r.test_count);
    r.test_code = DigitsOnly(r.test_code);
    r.mobile = FirstMobile(r.mobile);
}

// ────────────────────────── ۲) ارسال
var ok = new List<Suc>();
var err = new List<Err>();
var errorRows = new HashSet<int>();        // ← فقط شمارهٔ سطرهای خطادار
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(TIMEOUT) };

int done = 0, nextFlushMark = FLUSH_EVERY;

var groups = rows.GroupBy(r => new
{
    r.type,
    r.national_code,
    r.name,
    r.mobile,
    r.name_rabet_company,
    r.family_rabet_company
});

foreach (var g in groups)
{
    var root = new Dictionary<string, string>(AUTH)
    {
        ["type"] = g.Key.type,
        ["national_code"] = g.Key.national_code,
        ["name"] = g.Key.name,
        ["mobile"] = g.Key.mobile,
        ["name_rabet_company"] = g.Key.name_rabet_company,
        ["family_rabet_company"] = g.Key.family_rabet_company
    };

    var list = g.ToList();

    for (int i = 0; i < list.Count; i += MAX_PER)
    {
        var slice = list.Skip(i).Take(MAX_PER).ToList();
        var form = new List<KeyValuePair<string, string>>();
        form.AddRange(root);

        //for (int idx = 0; idx < slice.Count; idx++)
        //{
        //    var s = slice[idx];
        //    string p(string key) => $"services[{idx}][{key}]";

        //    form.Add(new(p("test_code"), s.test_code));
        //    form.Add(new(p("test_count"), s.test_count));
        //    form.Add(new(p("type_credit"), "2"));
        //    form.Add(new(p("tariffs_basis"), "2"));
        //    form.Add(new(p("price"), s.price));
        //    form.Add(new(p("date"), s.date));
        //    form.Add(new(p("discount"), s.discount));
        //}

        for (int idx = 0; idx < slice.Count; idx++)
        {
            var s = slice[idx];
            // نام کلید در فرم بر اساس tariffs_basis
            string fieldName = s.tariffs_basis == "2"
                             ? "test_count"
                             : "time_execute";

            // مقدار همیشه از test_count خوانده می‌شود
            string fieldValue = s.test_count;

            string p(string key) => $"services[{idx}][{key}]";

            form.Add(new(p("test_code"), s.test_code));
            form.Add(new(p(fieldName), fieldValue));       // ← این خط تغییر کرد
            form.Add(new(p("type_credit"), "2"));
            form.Add(new(p("tariffs_basis"), s.tariffs_basis)); // از CSV می‌آید
            form.Add(new(p("price"), s.price));
            form.Add(new(p("date"), s.date));
            form.Add(new(p("discount"), s.discount));
        }

        HttpResponseMessage resp;
        try { resp = await PostWithRetryAsync(http, API_URL, form); }
        catch (Exception ex)
        {
            var detail = ex.ToString();
            slice.ForEach(s =>
            {
                err.Add(new(s.excel_row, "HTTP", detail));
                errorRows.Add(s.excel_row);
            });
            continue;
        }

        JsonElement json;
        try
        {
            var txt = await resp.Content.ReadAsStringAsync();
            json = JsonSerializer.Deserialize<JsonElement>(txt);
        }
        catch (Exception ex)
        {
            slice.ForEach(s =>
            {
                err.Add(new(s.excel_row, "JSON", ex.ToString()));
                errorRows.Add(s.excel_row);
            });
            continue;
        }

        /* ───── تحلیل پاسخ ───── */
        int rootError = json.GetProperty("error").GetInt32();
        JsonElement resultArr = default;
        bool hasResult =
            rootError == 0 &&
            (json.TryGetProperty("result", out resultArr) ||
             json.TryGetProperty("response", out var respObj) &&
             respObj.TryGetProperty("result", out resultArr));

        if (hasResult)
        {
            var res = resultArr.EnumerateArray().ToArray();
            for (int k = 0; k < slice.Count; k++)
            {
                if (k >= res.Length)
                {
                    err.Add(new(slice[k].excel_row, "API", "no-match result"));
                    errorRows.Add(slice[k].excel_row);
                    continue;
                }

                var r = res[k];
                if (r.GetProperty("error").GetInt32() == 0)
                    ok.Add(new(slice[k].excel_row,
                               r.GetProperty("data").ToString()));
                else
                {
                    err.Add(new(slice[k].excel_row,
                                r.GetProperty("type").GetString() ?? "",
                                r.GetProperty("msg").GetString() ?? ""));
                    errorRows.Add(slice[k].excel_row);
                }
            }
        }
        else
        {
            string et = json.TryGetProperty("response", out var r1)
                         ? r1.GetProperty("type").GetString() ?? ""
                         : json.GetProperty("type").GetString() ?? "";
            string msg = json.TryGetProperty("response", out var r2)
                         ? r2.GetProperty("msg").GetString() ?? ""
                         : json.GetProperty("msg").GetString() ?? "";

            foreach (var s in slice)
            {
                err.Add(new(s.excel_row, et, msg));
                errorRows.Add(s.excel_row);
            }
        }
        /* ───── پایان تحلیل پاسخ ───── */

        done += slice.Count;
        Console.WriteLine($"{done:N0}/{rows.Count:N0}  ({done * 100.0 / rows.Count:F1}%)  ({errorRows.Count:N0})");

        if (done >= nextFlushMark)
        {
            FlushLogs(ok, err, errorRows);
            nextFlushMark += FLUSH_EVERY;
        }

        await Task.Delay(DELAY_MS);
    }
}

// ────────────────────────── ۳) خروجی نهایی
FlushLogs(ok, err, errorRows);
Console.WriteLine($"✓ Finished | Success: {ok.Count} | Errors: {err.Count}");

// ────────────────────────── لاگ‌نویسی
static void FlushLogs(IEnumerable<Suc> ok, IEnumerable<Err> er, HashSet<int> errRows)
{
    using (var w1 = new StreamWriter("success_log.csv", false, Encoding.UTF8))
    using (var c1 = new CsvWriter(w1, CultureInfo.InvariantCulture))
        c1.WriteRecords(ok.OrderBy(x => x.excel_row));

    using (var w2 = new StreamWriter("error_log.csv", false, Encoding.UTF8))
    using (var c2 = new CsvWriter(w2, CultureInfo.InvariantCulture))
        c2.WriteRecords(er.OrderBy(x => x.excel_row));

    File.WriteAllLines("error_rows.txt",
                       errRows.OrderBy(x => x).Select(x => x.ToString()));
}

// ────────────────────────── مدل‌های داده
record Row
{
    public string name { get; set; } = "";
    public string type { get; set; } = "";
    public string national_code { get; set; } = "";
    public string mobile { get; set; } = "";
    public string price { get; set; } = "";
    public string test_count { get; set; } = "";
    public string date { get; set; } = "";
    public string discount { get; set; } = "";
    public string test_code { get; set; } = "";
    public string name_rabet_company { get; set; } = "";
    public string family_rabet_company { get; set; } = "";

    // ← این خاصیت را اضافه کنید:
    public string tariffs_basis { get; set; } = "";

    [CsvHelper.Configuration.Attributes.Ignore]
    public int excel_row { get; set; }
}
record Suc(int excel_row, string service_id);
record Err(int excel_row, string error_type, string msg);

