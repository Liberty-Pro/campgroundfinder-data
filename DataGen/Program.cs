using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CampgroundFinder.DataGen;

// ============================================================================
// ONE-TIME (well - occasionally re-run) BULK DATA PULL, added 2026-08-27 as
// the real fix for BOTH halves of bug #7:
//   (a) real NPS campgrounds (Mazama/Lost Creek/Cave Creek) missing from
//       search entirely, and
//   (b) non-NPS facilities (Fish Lake, Fourmile Lake, Doe Point, Big Elk
//       Guard Station, Lodgepole Guard Station, ...) wrongly labeled NPS.
//
// WHY THIS EXISTS INSTEAD OF MORE LIVE-SEARCH FIXES: this project has now
// confirmed, via real device/browser tests, THREE separate unreliable pieces
// of RIDB's live filtering:
//   1. /facilities geo-radius search misses facilities with broken (0,0)
//      coordinates (e.g. Mazama itself).
//   2. /recareas RADIUS filter doesn't actually filter by radius (identical
//      results at 2x radius - confirmed decisively).
//   3. /recareas STATE filter excludes at least Crater Lake National Park's
//      own RecArea entirely from state=OR (confirmed - zero "2647" matches
//      anywhere in the full state-scoped response).
// Rather than chase a fourth live-filter workaround, this tool pulls RIDB's
// ENTIRE national RecArea and Facility tables via plain offset/limit
// pagination (no radius, no state, no activity filter - the most basic kind
// of pagination there is, and the only kind proven reliable so far: TOTAL_
// COUNT genuinely changed between OR's 179 RecAreas and CA's 347 in the same
// pull, so plain pagination at least reports real, differentiated counts).
// The app then does ALL discovery (radius) and classification (agency)
// filtering itself, client-side, against this local snapshot - exactly the
// "don't trust RIDB's server-side filtering" principle already proven
// necessary everywhere else in this app.
//
// AGENCY MAPPING: sourced from a REAL, COMPLETE dump of RIDB's own
// /organizations endpoint that Nick pulled directly (both pages, TOTAL_COUNT
// 33, 20+13 = 33 confirmed complete - not a guess). See OrgIdToSource below.
//
// This tool is NOT part of the shipped app - CampgroundFinder.csproj does
// not reference it, and its output (a compressed JSON snapshot) only enters
// the app when someone manually copies it into
// CampgroundFinder/Resources/Raw/. See README.md in this folder.
// ============================================================================

internal static class Program
{
    const string BaseUrl = "https://ridb.recreation.gov/api/v1";
    const int RequestedPageSize = 500; // RIDB may clamp this - we detect and adapt, see FetchPageAsync's caller.

    // Confirmed 2026-08-27 via the real, complete /organizations dump (33 of
    // 33 orgs, both pages). Only the four agencies this app actually
    // supports (matches Models/Enums.cs SourceType) are mapped - everything
    // else (127 FWS, 129 BOR, 133 DOT/National Scenic Byways, every state
    // park org, Smithsonian, NARA, military branches, etc.) is real RIDB
    // data but out of this app's scope, so it's simply dropped during the
    // facility pull. Notably 133 = Department of Transportation, NOT a land
    // management agency at all - it's RIDB's National Scenic Byway program,
    // filed under DOT even when the byway physically runs through USFS/NPS/
    // BLM land. That's exactly the kind of record the OLD text-sniffing
    // GuessAgency() heuristic could never have told apart from a real land
    // manager, and is now a non-issue since we key off the real ParentOrgID
    // instead of guessing from name/description text.
    static readonly Dictionary<string, string> OrgIdToSource = new()
    {
        ["126"] = "Blm",
        ["128"] = "Nps",
        ["130"] = "Coe",
        ["131"] = "Usfs",
    };

    // Purely for readable audit output below - not used for any filtering decision.
    static readonly Dictionary<string, string> KnownOtherOrgNames = new()
    {
        ["127"] = "FWS", ["129"] = "BOR", ["133"] = "DOT (National Scenic Byways)",
        ["132"] = "TVA", ["135"] = "Smithsonian", ["139"] = "DOI (parent label)",
        ["157"] = "FEDERAL (label)", ["240"] = "STATE PARKS (label)",
    };

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // COMPLETENESS AUDIT, added 2026-08-27 at Nick's request: before trusting
    // this bundle as "the complete nationwide list" of NPS/USFS/BLM/COE
    // campgrounds, we need real evidence the filters below aren't silently
    // dropping real camping facilities - either because RIDB worded a
    // facility's type differently than "Campground"/"Camping" (the type
    // filter), or because a real camping facility sits under an OrgID outside
    // the 4 we mapped (the org filter). These two dictionaries count every
    // facility EXCLUDED by each filter, keyed by whatever value caused the
    // exclusion, so the end-of-run summary can show exactly what got left out
    // and how often - instead of just asserting completeness.
    static readonly Dictionary<string, int> ExcludedByTypeCounts = new();
    static readonly Dictionary<string, int> ExcludedByOrgCounts = new();
    static readonly Dictionary<string, List<string>> ExcludedTypeSamples = new();
    static int RecoveredByNameCount = 0;
    static readonly List<string> RecoveredByNameSamples = new();

    static async Task<int> Main(string[] args)
    {
        var apiKey = ResolveApiKey(args);
        if (apiKey is null)
        {
            Console.Error.WriteLine("No RIDB API key found. Pass one as the first argument:");
            Console.Error.WriteLine("  dotnet run -- YOUR_API_KEY");
            Console.Error.WriteLine("...or run this from a checkout where ../CampgroundFinder/Services/ApiKeyDefaults.cs still has the real key baked in (it will be read automatically).");
            return 1;
        }

        var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
        Directory.CreateDirectory(dataDir);

        var recAreasNdjson = Path.Combine(dataDir, "recareas.ndjson");
        var recAreasCheckpoint = Path.Combine(dataDir, "recareas.checkpoint");
        var facilitiesNdjson = Path.Combine(dataDir, "facilities.ndjson");
        var facilitiesCheckpoint = Path.Combine(dataDir, "facilities.checkpoint");
        var facilityLinksNdjson = Path.Combine(dataDir, "facility_links.ndjson");

        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Console.WriteLine("=== Step 1/4: pulling every RecArea nationwide (plain pagination, no filters) ===");
        await RunPagedFetchAsync("RecAreas", "/recareas", apiKey, recAreasNdjson, recAreasCheckpoint, MapRecArea, cts.Token);

        Console.WriteLine();
        Console.WriteLine("=== Step 2/4: pulling every Facility nationwide (plain pagination, no filters) ===");
        Console.WriteLine("(This is the big one - RIDB's total facility count across ALL types/agencies. Filtering to camping-type + our 4 agencies happens per-record as we go, so the .ndjson file only grows with facilities we'll actually use.)");
        await RunPagedFetchAsync("Facilities", "/facilities", apiKey, facilitiesNdjson, facilitiesCheckpoint, MapFacility, cts.Token);

        Console.WriteLine();
        Console.WriteLine("=== Step 3/4: fetching each facility's own \"Official Web Site\" link ===");
        Console.WriteLine("(One RIDB call per kept facility - RIDB's own FacilityMapURL is blank for ~99% of facilities, but /facilities/{id}/links often has a real official site link RIDB's flat /facilities table doesn't expose. This is the slow step - budget roughly 15-25 minutes for ~5,900 facilities at a polite pace. Resumable: safe to Ctrl+C and re-run, already-fetched facilities are skipped.)");
        await FetchFacilityLinksAsync(facilitiesNdjson, apiKey, facilityLinksNdjson, cts.Token);

        Console.WriteLine();
        Console.WriteLine("=== Step 4/4: joining + building the compact bundle the app will ship ===");
        var (bundlePath, gzPath) = await BuildBundleAsync(dataDir, recAreasNdjson, facilitiesNdjson, facilityLinksNdjson, cts.Token);

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"Done in {sw.Elapsed.TotalMinutes:0.0} minutes.");
        Console.WriteLine($"Bundle (uncompressed, for inspection): {bundlePath} ({new FileInfo(bundlePath).Length / 1024:N0} KB)");
        Console.WriteLine($"Bundle (gzip, this is what ships):     {gzPath} ({new FileInfo(gzPath).Length / 1024:N0} KB)");

        PrintCompletenessAudit();

        Console.WriteLine();
        Console.WriteLine("Next: copy the .json.gz file into CampgroundFinder/Resources/Raw/ (tell Claude it's ready and it'll wire up the app-side loading code).");

        return 0;
    }

    // COMPLETENESS AUDIT summary, added 2026-08-27 (see the dictionaries'
    // doc comment above for why). Prints what got excluded and how often, so
    // "is this really the complete nationwide list" has real evidence behind
    // it instead of an assumption. Read this looking for anything that
    // sounds like real camping (a FacilityTypeDescription like "Campsite" or
    // "RV Site" instead of "Campground"/"Camping" would show up in the first
    // list; a high count under an unfamiliar OrgID in the second list would
    // be worth cross-checking against the /organizations dump).
    static void PrintCompletenessAudit()
    {
        Console.WriteLine();
        Console.WriteLine("--- Completeness audit ---");

        Console.WriteLine($"Facilities RECOVERED from the generic \"Facility\"/blank type bucket because their own name says campground/campsite/camping: {RecoveredByNameCount:N0}");
        foreach (var name in RecoveredByNameSamples)
            Console.WriteLine($"              - {name}");
        if (RecoveredByNameCount > RecoveredByNameSamples.Count)
            Console.WriteLine($"              ...and {RecoveredByNameCount - RecoveredByNameSamples.Count:N0} more not shown.");
        Console.WriteLine();

        Console.WriteLine($"Distinct FacilityTypeDescription values EXCLUDED by the type filter (top 30 by count, with up to 15 sample names each):");
        foreach (var (type, count) in ExcludedByTypeCounts.OrderByDescending(kv => kv.Value).Take(30))
        {
            Console.WriteLine($"  {count,8:N0}  {type}");
            if (ExcludedTypeSamples.TryGetValue(type, out var samples))
                foreach (var name in samples)
                    Console.WriteLine($"              - {name}");
        }
        if (ExcludedByTypeCounts.Count > 30)
            Console.WriteLine($"  ...and {ExcludedByTypeCounts.Count - 30:N0} more distinct type descriptions not shown.");

        Console.WriteLine();
        Console.WriteLine($"OrgIDs of otherwise-campground-typed facilities EXCLUDED by the agency filter (all, by count):");
        foreach (var (orgId, count) in ExcludedByOrgCounts.OrderByDescending(kv => kv.Value))
        {
            var label = KnownOtherOrgNames.TryGetValue(orgId, out var known) ? known : "(unrecognized org id - cross-check against /organizations)";
            Console.WriteLine($"  {count,8:N0}  OrgID {orgId,-6} {label}");
        }
    }

    static string? ResolveApiKey(string[] args)
    {
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            return args[0].Trim();

        try
        {
            var candidate = Path.Combine(Directory.GetCurrentDirectory(), "..", "CampgroundFinder", "Services", "ApiKeyDefaults.cs");
            if (File.Exists(candidate))
            {
                var text = File.ReadAllText(candidate);
                var match = System.Text.RegularExpressions.Regex.Match(text, "RidbApiKey\\s*=\\s*\"([^\"]+)\"");
                if (match.Success)
                    return match.Groups[1].Value;
            }
        }
        catch
        {
            // fall through to null - caller prints the "no key" message
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Generic paginated-pull driver. Resumable: writes the next offset to
    // fetch to a checkpoint file after every successful page, and advances
    // the offset by however many rows the server ACTUALLY returned (not the
    // nominal page size requested) - this defends against RIDB silently
    // clamping `limit` to something smaller, which would otherwise cause
    // silently-skipped records if we assumed our requested page size was
    // honored.
    // ------------------------------------------------------------------
    static async Task RunPagedFetchAsync(
        string label,
        string endpointPath,
        string apiKey,
        string ndjsonPath,
        string checkpointPath,
        Func<JsonElement, string?> mapAndFilter,
        CancellationToken ct)
    {
        var offset = 0;
        if (File.Exists(checkpointPath) && int.TryParse(await File.ReadAllTextAsync(checkpointPath, ct), out var savedOffset))
        {
            offset = savedOffset;
            Console.WriteLine($"[{label}] Resuming from offset {offset} (checkpoint file found).");
        }

        int? totalCount = null;
        await using var stream = new FileStream(ndjsonPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, Encoding.UTF8);

        while (true)
        {
            var (total, rows) = await FetchPageAsync(endpointPath, apiKey, RequestedPageSize, offset, ct);
            totalCount ??= total;

            if (rows.Count == 0)
            {
                Console.WriteLine($"[{label}] offset={offset}: 0 rows returned - stopping (either done, or the pull is somehow ahead of TOTAL_COUNT).");
                break;
            }

            var kept = 0;
            foreach (var row in rows)
            {
                var line = mapAndFilter(row);
                if (line is null) continue;
                await writer.WriteLineAsync(line);
                kept++;
            }
            await writer.FlushAsync(ct);

            offset += rows.Count;
            await File.WriteAllTextAsync(checkpointPath, offset.ToString(), ct);

            Console.WriteLine($"[{label}] offset={offset}/{totalCount} ({rows.Count} raw this page, {kept} kept)");

            if (offset >= totalCount)
            {
                Console.WriteLine($"[{label}] Reached TOTAL_COUNT ({totalCount}). Done.");
                break;
            }

            await Task.Delay(150, ct); // be polite to a free government API - no documented rate limit was found, so this is a deliberately conservative default.
        }
    }

    static async Task<(int totalCount, List<JsonElement> rows)> FetchPageAsync(string endpointPath, string apiKey, int limit, int offset, CancellationToken ct)
    {
        var url = $"{BaseUrl}{endpointPath}?limit={limit}&offset={offset}";
        var attempt = 0;

        while (true)
        {
            attempt++;
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", apiKey);
                response = await Http.SendAsync(request, ct);
            }
            catch (Exception ex) when (attempt <= 8)
            {
                var delay = BackoffDelay(attempt);
                Console.WriteLine($"  [retry] network error at offset={offset}: {ex.Message}. Retrying in {delay.TotalSeconds:0}s (attempt {attempt}/8).");
                await Task.Delay(delay, ct);
                continue;
            }

            if (((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) && attempt <= 8)
            {
                var delay = BackoffDelay(attempt);
                Console.WriteLine($"  [retry] HTTP {(int)response.StatusCode} at offset={offset}. Retrying in {delay.TotalSeconds:0}s (attempt {attempt}/8).");
                response.Dispose();
                await Task.Delay(delay, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            var totalCount = doc.RootElement.TryGetProperty("METADATA", out var meta) &&
                meta.TryGetProperty("RESULTS", out var mr) &&
                mr.TryGetProperty("TOTAL_COUNT", out var tc) &&
                tc.TryGetInt32(out var t)
                ? t
                : 0;

            var rows = new List<JsonElement>();
            if (doc.RootElement.TryGetProperty("RECDATA", out var data))
            {
                foreach (var el in data.EnumerateArray())
                    rows.Add(el.Clone()); // .Clone() so it survives past `doc`'s disposal
            }

            return (totalCount, rows);
        }
    }

    static TimeSpan BackoffDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));

    // ------------------------------------------------------------------
    // ADDED 2026-08-28 (Bug #11 follow-up): Nick asked whether a broken/absent
    // recreation.gov "Book" link could instead point at the campground's own
    // agency page (e.g. nps.gov) - the exact example he gave, Lost Creek
    // Campground/Crater Lake (facility 258613), turned out to have exactly
    // that: RIDB's flat /facilities table (what MapFacility above reads) has
    // NO such link - FacilityMapURL is blank for 5,875 of this bundle's 5,929
    // facilities (99%). But RIDB has a SEPARATE per-facility endpoint,
    // /facilities/{id}/links, that most facilities don't expose any other
    // way - confirmed directly for 258613, which returns exactly
    // "https://www.nps.gov/crla/planyourvisit/lost_creek.htm" (LinkType
    // "Official Web Site"). Spot-checked across all 4 agencies this app
    // supports: NPS and USFS both consistently use the LinkType string
    // "Official Web Site" for the facility's own page; BLM instead uses a
    // numeric LinkType ("3") but the right entry's Title matches the facility
    // name exactly; COE is the least consistent - one test facility had a
    // proper "Official Web Site" entry (pointing at the parent lake/reservoir,
    // not the specific campground - still useful), another had no official
    // link at all, just unrelated state-tourism links. So the picking rule
    // below prefers an explicit "Official Web Site" LinkType first, then
    // falls back to a link whose Title matches the facility's own name -
    // and returns null (no link attached) rather than guessing when neither
    // matches, since attaching a wrong link (e.g. a Kentucky tourism site, or
    // a nearby but different trail) would be worse than no link.
    //
    // This is a per-facility call - no bulk/paginated equivalent exists - so
    // it's the slow step of a regen (~5,900 calls at a polite pace). Written
    // as its own resumable pass (append-only ndjson, existing IDs skipped on
    // resume) for the same reason RunPagedFetchAsync above is checkpointed:
    // a run this size WILL get interrupted sometimes.
    // ------------------------------------------------------------------
    static async Task FetchFacilityLinksAsync(string facilitiesNdjsonPath, string apiKey, string linksNdjsonPath, CancellationToken ct)
    {
        // Gather the distinct (id, name) pairs we actually need links for -
        // read from facilities.ndjson (already filtered to camping-type,
        // in-scope-agency facilities by MapFacility). A resumed facilities
        // pull can contain duplicate lines for the same ID (a page re-fetched
        // after an interrupted checkpoint write) - dedupe here too, same as
        // BuildBundleAsync does.
        var facilities = new Dictionary<string, string>(); // id -> name
        await foreach (var line in File.ReadLinesAsync(facilitiesNdjsonPath, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            facilities[id] = name;
        }

        var alreadyDone = new HashSet<string>();
        if (File.Exists(linksNdjsonPath))
        {
            await foreach (var line in File.ReadLinesAsync(linksNdjsonPath, ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                alreadyDone.Add(doc.RootElement.GetProperty("id").GetString()!);
            }
            if (alreadyDone.Count > 0)
                Console.WriteLine($"[FacilityLinks] Resuming - {alreadyDone.Count:N0} of {facilities.Count:N0} facilities already fetched.");
        }

        var toFetch = facilities.Where(kv => !alreadyDone.Contains(kv.Key)).ToList();
        var foundCount = 0;
        var processed = 0;

        await using var stream = new FileStream(linksNdjsonPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);

        foreach (var (id, name) in toFetch)
        {
            ct.ThrowIfCancellationRequested();
            var infoUrl = await FetchBestOfficialLinkAsync(id, name, apiKey, ct);
            if (infoUrl is not null) foundCount++;

            await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, infoUrl }));
            processed++;

            if (processed % 200 == 0)
            {
                await writer.FlushAsync(ct);
                Console.WriteLine($"[FacilityLinks] {alreadyDone.Count + processed:N0}/{facilities.Count:N0} facilities checked ({foundCount:N0} found so far this run).");
            }

            await Task.Delay(100, ct); // one call per facility - keep this polite, there are thousands of them.
        }

        await writer.FlushAsync(ct);
        Console.WriteLine($"[FacilityLinks] Done - {foundCount:N0} official links found out of {toFetch.Count:N0} facilities checked this run.");
    }

    static readonly HashSet<string> AcceptableOtherLinkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Official Web Site", "Facility Home Page",
    };

    static async Task<string?> FetchBestOfficialLinkAsync(string facilityId, string facilityName, string apiKey, CancellationToken ct)
    {
        var url = $"{BaseUrl}/facilities/{facilityId}/links?limit=1000";
        var attempt = 0;

        while (true)
        {
            attempt++;
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", apiKey);
                response = await Http.SendAsync(request, ct);
            }
            catch (Exception ex) when (attempt <= 5)
            {
                var delay = BackoffDelay(attempt);
                Console.WriteLine($"  [retry] network error fetching links for facility {facilityId}: {ex.Message}. Retrying in {delay.TotalSeconds:0}s (attempt {attempt}/5).");
                await Task.Delay(delay, ct);
                continue;
            }

            if (((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) && attempt <= 5)
            {
                var delay = BackoffDelay(attempt);
                Console.WriteLine($"  [retry] HTTP {(int)response.StatusCode} fetching links for facility {facilityId}. Retrying in {delay.TotalSeconds:0}s (attempt {attempt}/5).");
                response.Dispose();
                await Task.Delay(delay, ct);
                continue;
            }

            // A 404 here means this facility ID has no /links resource at all
            // (seen for some facilities) - that's a real, expected "no link"
            // outcome, not a transient failure.
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException) { return null; } // malformed/unexpected body - skip rather than crash a multi-hour run
            using var _doc = doc;

            if (!doc.RootElement.TryGetProperty("RECDATA", out var links)) return null;

            var normalizedName = NormalizeForMatch(facilityName);
            string? byExactType = null;
            string? byNameMatch = null;

            foreach (var link in links.EnumerateArray())
            {
                var linkType = link.TryGetProperty("LinkType", out var lt) ? lt.GetString() ?? "" : "";
                var title = link.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
                var linkUrl = link.TryGetProperty("URL", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(linkUrl)) continue;

                if (byExactType is null && AcceptableOtherLinkTypes.Contains(linkType))
                    byExactType = linkUrl;

                // BLM (and some others) don't use the "Official Web Site" LinkType
                // string at all - confirmed for Mattole Campground, whose real
                // self-link uses LinkType "3" but has a Title exactly matching the
                // facility's own name. Fall back to a name match so those aren't
                // missed, but require the normalized title to actually CONTAIN the
                // facility's normalized name (not just any link) to avoid picking
                // up an unrelated nearby trail/permit/tourism link, which RIDB's
                // /links data otherwise regularly includes alongside the real one.
                if (byNameMatch is null && normalizedName.Length > 0 &&
                    NormalizeForMatch(title).Contains(normalizedName))
                    byNameMatch = linkUrl;
            }

            return byExactType ?? byNameMatch;
        }
    }

    static string NormalizeForMatch(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

    // ------------------------------------------------------------------
    // Per-record mapping. Field extraction mirrors IRidbService.cs's existing
    // parsing exactly (same GetString/GetRawText-for-string-IDs handling,
    // same nullable-number handling) so behavior stays consistent with the
    // app's own conventions.
    // ------------------------------------------------------------------
    static string? MapRecArea(JsonElement ra)
    {
        var id = GetIdString(ra, "RecAreaID");
        if (id is null) return null;

        var name = ra.TryGetProperty("RecAreaName", out var n) ? n.GetString() ?? "" : "";
        var lat = GetNullableDouble(ra, "RecAreaLatitude");
        var lon = GetNullableDouble(ra, "RecAreaLongitude");

        return JsonSerializer.Serialize(new { id, name, lat, lon });
    }

    static string? MapFacility(JsonElement f)
    {
        var typeDesc = f.TryGetProperty("FacilityTypeDescription", out var ft) ? ft.GetString() ?? "" : "";
        var name0 = f.TryGetProperty("FacilityName", out var fn0) ? fn0.GetString() ?? "" : "";
        var isCampgroundType = typeDesc.Contains("Campground", StringComparison.OrdinalIgnoreCase) ||
                                typeDesc.Contains("Camping", StringComparison.OrdinalIgnoreCase);

        // RECOVERY CHECK, added 2026-08-27: a completeness audit (run at Nick's
        // request) turned up a real miss - "Bridge Bay Campground" (a genuine,
        // well-known NPS campground in Yellowstone) is typed generically as
        // "Facility" in RIDB rather than "Campground"/"Camping", so the plain type
        // check above was silently dropping it. Sampling showed every OTHER
        // excluded type (Visitor Center, Permit, Ticket Facility, Tree Permit,
        // Timed Entry, Venue Reservations, Cemetery and Memorial, Library, Museum,
        // Archives, etc.) is genuinely not camping-related - none of those 15-per-
        // type samples read like a real campground. So this recovery is
        // deliberately scoped ONLY to the ambiguous "Facility"/blank-type bucket
        // (where a real campground could plausibly be hiding behind a generic
        // label), not loosened for the other, well-evidenced-as-irrelevant types -
        // a facility here is recovered only if its own NAME contains "campground",
        // "campsite", or "camping".
        var isAmbiguousType = !isCampgroundType &&
            (typeDesc.Trim().Equals("Facility", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(typeDesc));
        var recoveredByName = isAmbiguousType &&
            (name0.Contains("campground", StringComparison.OrdinalIgnoreCase) ||
             name0.Contains("campsite", StringComparison.OrdinalIgnoreCase) ||
             name0.Contains("camping", StringComparison.OrdinalIgnoreCase));

        if (recoveredByName)
        {
            RecoveredByNameCount++;
            if (RecoveredByNameSamples.Count < 30) RecoveredByNameSamples.Add(name0);
        }

        if (!isCampgroundType && !recoveredByName)
        {
            var typeKey = string.IsNullOrWhiteSpace(typeDesc) ? "(blank FacilityTypeDescription)" : typeDesc;
            ExcludedByTypeCounts[typeKey] = ExcludedByTypeCounts.GetValueOrDefault(typeKey) + 1;

            // Sample a handful of real names per excluded type (capped at 15) so the
            // audit summary can be eyeballed for anything that sounds like a real
            // campground hiding under a generic/catch-all type label like "Facility" -
            // added 2026-08-27 specifically because that bucket alone was 8,205
            // records (more than half of RIDB's entire national facility table) and a
            // raw count alone can't tell us whether that's boat ramps and trailheads
            // or actual mislabeled campgrounds.
            if (!ExcludedTypeSamples.TryGetValue(typeKey, out var samples))
                ExcludedTypeSamples[typeKey] = samples = new List<string>();
            if (samples.Count < 15)
                samples.Add(string.IsNullOrEmpty(name0) ? "(unnamed)" : name0);

            return null; // not a campground-type facility - out of scope for this app
        }

        var orgId = GetIdString(f, "ParentOrgID");
        if (orgId is null || !OrgIdToSource.TryGetValue(orgId, out var source))
        {
            // Only reached for a facility whose TYPE looks like real camping -
            // so this specifically tells us about camping facilities excluded
            // for agency-scope reasons, not type-wording reasons.
            var orgKey = orgId is null ? "(missing ParentOrgID)" : orgId;
            ExcludedByOrgCounts[orgKey] = ExcludedByOrgCounts.GetValueOrDefault(orgKey) + 1;
            return null; // agency this app doesn't cover (FWS/BOR/state/DOT-byway/etc.) or missing ParentOrgID
        }

        var id = GetIdString(f, "FacilityID");
        if (id is null) return null;

        var name = f.TryGetProperty("FacilityName", out var fn) ? fn.GetString() ?? "Unknown" : "Unknown";
        var desc = f.TryGetProperty("FacilityDescription", out var fd) ? fd.GetString() : null;
        var mapUrl = f.TryGetProperty("FacilityMapURL", out var mu) ? mu.GetString() : null;
        var lat = GetNullableDouble(f, "FacilityLatitude");
        var lon = GetNullableDouble(f, "FacilityLongitude");
        bool? reservable = f.TryGetProperty("Reservable", out var rv) &&
            (rv.ValueKind == JsonValueKind.True || rv.ValueKind == JsonValueKind.False)
            ? rv.GetBoolean()
            : null;
        var recAreaId = GetIdString(f, "ParentRecAreaID");

        // Added 2026-08-27, second half of bug #7's fallout: some facilities
        // RIDB tags Reservable=false aren't actually first-come-first-served -
        // they're reservable, just through a DIFFERENT system than
        // recreation.gov (confirmed for Jedediah Smith Campground, part of
        // Redwood National and State Parks but really booked via
        // reservecalifornia.com - RIDB's own FacilityReservationURL field
        // gave the real answer directly: "https://www.reservecalifornia.com/").
        // Captured for every kept facility (not just Reservable=false ones) so
        // the app can tell "no reservation system exists" apart from
        // "reservation system exists, just isn't ours" instead of guessing.
        var reservationUrl = f.TryGetProperty("FacilityReservationURL", out var fru) && fru.ValueKind == JsonValueKind.String
            ? fru.GetString()
            : null;

        return JsonSerializer.Serialize(new { id, name, desc, mapUrl, lat, lon, reservable, source, recAreaId, reservationUrl });
    }

    static string? GetIdString(JsonElement el, string propertyName) =>
        el.TryGetProperty(propertyName, out var prop)
            ? (prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText())
            : null;

    static double? GetNullableDouble(JsonElement el, string propertyName) =>
        el.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetDouble()
            : null;

    // ------------------------------------------------------------------
    // Join step: for each kept facility, attach its parent RecArea's name +
    // coordinates (used by the app as a fallback when a facility's own
    // coordinates are missing/zero - the exact problem that hid Mazama).
    // Dedupes facilities by ID (defensive - a resumed run could have
    // duplicate lines from a page that was re-fetched after an interrupted
    // checkpoint write).
    // ------------------------------------------------------------------
    static async Task<(string bundlePath, string gzPath)> BuildBundleAsync(string dataDir, string recAreasNdjson, string facilitiesNdjson, string facilityLinksNdjson, CancellationToken ct)
    {
        // Bug #11 follow-up, 2026-08-28: id -> the best "official site" link
        // found by FetchFacilityLinksAsync above (null when none was found).
        // Missing from the dictionary entirely just means that facility ID
        // was never processed (e.g. the links pass was interrupted) - the app
        // side treats a missing/null infoUrl the same way either way.
        var facilityLinks = new Dictionary<string, string?>();
        if (File.Exists(facilityLinksNdjson))
        {
            await foreach (var line in File.ReadLinesAsync(facilityLinksNdjson, ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var id = doc.RootElement.GetProperty("id").GetString()!;
                var infoUrl = doc.RootElement.TryGetProperty("infoUrl", out var iu) && iu.ValueKind == JsonValueKind.String ? iu.GetString() : null;
                facilityLinks[id] = infoUrl;
            }
        }
        Console.WriteLine($"Loaded {facilityLinks.Count:N0} facility-links lookups ({facilityLinks.Values.Count(v => v is not null):N0} with a real official-site URL found).");

        var recAreas = new Dictionary<string, (string Name, double? Lat, double? Lon)>();
        await foreach (var line in File.ReadLinesAsync(recAreasNdjson, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString()!;
            var name = root.GetProperty("name").GetString() ?? "";
            double? lat = root.GetProperty("lat").ValueKind == JsonValueKind.Number ? root.GetProperty("lat").GetDouble() : null;
            double? lon = root.GetProperty("lon").ValueKind == JsonValueKind.Number ? root.GetProperty("lon").GetDouble() : null;
            recAreas[id] = (name, lat, lon);
        }
        Console.WriteLine($"Loaded {recAreas.Count:N0} RecAreas for the coordinate-fallback join.");

        var seen = new HashSet<string>();
        var bundleFacilities = new List<object>();
        var sourceCounts = new Dictionary<string, int>();
        var npsRecAreaNames = new HashSet<string>();
        var craterLakeFound = false;
        var mazamaFound = false;
        var externalReservationCount = 0;
        var infoUrlFoundCount = 0;

        await foreach (var line in File.ReadLinesAsync(facilitiesNdjson, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString()!;
            if (!seen.Add(id)) continue; // dedupe

            var name = root.GetProperty("name").GetString() ?? "Unknown";
            var desc = root.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var mapUrl = root.TryGetProperty("mapUrl", out var mu) && mu.ValueKind == JsonValueKind.String ? mu.GetString() : null;
            double? lat = root.TryGetProperty("lat", out var la) && la.ValueKind == JsonValueKind.Number ? la.GetDouble() : null;
            double? lon = root.TryGetProperty("lon", out var lo) && lo.ValueKind == JsonValueKind.Number ? lo.GetDouble() : null;
            bool? reservable = root.TryGetProperty("reservable", out var rv) && rv.ValueKind is JsonValueKind.True or JsonValueKind.False ? rv.GetBoolean() : null;
            var source = root.GetProperty("source").GetString()!;
            var recAreaId = root.TryGetProperty("recAreaId", out var ridEl) && ridEl.ValueKind == JsonValueKind.String ? ridEl.GetString() : null;
            var reservationUrl = root.TryGetProperty("reservationUrl", out var ru) && ru.ValueKind == JsonValueKind.String ? ru.GetString() : null;

            string? recAreaName = null;
            double? recLat = null, recLon = null;
            if (recAreaId is not null && recAreas.TryGetValue(recAreaId, out var ra))
            {
                recAreaName = ra.Name;
                recLat = ra.Lat;
                recLon = ra.Lon;
            }

            var infoUrl = facilityLinks.TryGetValue(id, out var iu) ? iu : null;

            sourceCounts[source] = sourceCounts.GetValueOrDefault(source) + 1;
            if (source == "Nps" && recAreaName is not null) npsRecAreaNames.Add(recAreaName);
            if (recAreaId == "2647") craterLakeFound = true;
            if (name.Contains("Mazama", StringComparison.OrdinalIgnoreCase)) mazamaFound = true;
            if (!string.IsNullOrWhiteSpace(reservationUrl) && !reservationUrl.Contains("recreation.gov", StringComparison.OrdinalIgnoreCase))
                externalReservationCount++;
            if (infoUrl is not null) infoUrlFoundCount++;

            bundleFacilities.Add(new { id, name, desc, mapUrl, lat, lon, reservable, source, recAreaName, recLat, recLon, reservationUrl, infoUrl });
        }

        var bundle = new
        {
            generatedUtc = DateTime.UtcNow.ToString("O"),
            facilityCount = bundleFacilities.Count,
            facilities = bundleFacilities,
        };

        var bundlePath = Path.Combine(dataDir, "campgrounds_bundle.json");
        var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(bundlePath, json, ct);

        var gzPath = bundlePath + ".gz";
        await using (var fileStream = File.Create(gzPath))
        await using (var gzStream = new GZipStream(fileStream, CompressionLevel.Optimal))
        await using (var writer = new StreamWriter(gzStream, Encoding.UTF8))
        {
            await writer.WriteAsync(json);
        }

        Console.WriteLine();
        Console.WriteLine("--- Bundle summary ---");
        foreach (var (src, count) in sourceCounts.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {src,-6} {count,6:N0} facilities");
        Console.WriteLine($"  TOTAL  {bundleFacilities.Count,6:N0} facilities across {recAreas.Count:N0} RecAreas scanned");
        Console.WriteLine($"  Distinct NPS-source RecAreas represented: {npsRecAreaNames.Count:N0}");
        Console.WriteLine($"  Facilities with a real, non-recreation.gov reservation URL: {externalReservationCount:N0} (these get \"reserve elsewhere\" handling instead of a live Recreation.gov availability check)");
        Console.WriteLine($"  Facilities with a real official-site link found via /links (infoUrl): {infoUrlFoundCount:N0} of {bundleFacilities.Count:N0}");
        Console.WriteLine();
        Console.WriteLine($"  Crater Lake NP (RecArea 2647) represented in bundle: {(craterLakeFound ? "YES" : "NO - investigate before shipping")}");
        Console.WriteLine($"  A facility named \"Mazama...\" present:              {(mazamaFound ? "YES" : "NO - investigate before shipping")}");

        return (bundlePath, gzPath);
    }
}
