using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pCrawling;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pTransport;
using I2PAtlas.Seeds;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pHostDiscoveryTests
{
    private const string UnknownB32 = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzq.b32.i2p";
    private static readonly I2pSeed Parent = Seed("parent.i2p");

    [TestMethod]
    public void ValidHtmlProducesOnlyNormalizedDepthOneHostAndStableReferrer()
    {
        var input = Observation("http://FOO.I2P./PRIVATE_PATH?q=PRIVATE_QUERY#PRIVATE_FRAGMENT", "http://foo.i2p:80/other");
        var result = Select(Snapshot(Parent), [Evidence(Parent, input)]);
        var item = result.WorkItems.Single();
        Assert.AreEqual("foo.i2p", item.Target.Hostname);
        Assert.AreEqual(I2pDiscoveredHostForm.Friendly, item.Target.HostForm);
        Assert.AreEqual(1, item.Depth);
        Assert.AreEqual(I2pCrawlReferrer.FromSeed(Parent), item.FirstReferrer);
        Assert.AreEqual(2L, result.EligibleUnknownReferenceCount);
        Assert.IsFalse(result.AdmissionLimitApplied);
        Assert.AreEqual("http://foo.i2p/robots.txt", new I2pProbeRequest(item.Target, ProbeResource.Robots).RequestUri.AbsoluteUri);
        Assert.AreEqual("http://foo.i2p/", new I2pProbeRequest(item.Target, ProbeResource.Root).RequestUri.AbsoluteUri);
        // The input observation remains intact; returned work retains none of its URL evidence.
        Assert.Contains("PRIVATE_QUERY", input.HomepageMetadata!.Links[0].Url);
        CollectionAssert.AreEquivalent(new[] { "Target", "FirstReferrer", "Depth" },
            typeof(I2pCrawlWorkItem).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => p.GetMethod?.IsPrivate != true && p.GetMethod?.IsFamily != true)
                .Select(p => p.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "Hostname", "DestinationHostname", "HostForm" },
            typeof(I2pCrawlReferrer).GetProperties().Select(p => p.Name).ToArray());
    }

    [TestMethod]
    [DataRow("text/html", true)]
    [DataRow("TEXT/HTML; CHARSET=\"utf-8\"", true)]
    [DataRow("text/html; charset=iso-8859-1", true)]
    [DataRow("application/xhtml+xml", true)]
    [DataRow("APPLICATION/XHTML+XML; charset=utf-8", true)]
    [DataRow(null, false)][DataRow("", false)]
    [DataRow("text/plain", false)][DataRow("text/xml", false)][DataRow("text/css", false)]
    [DataRow("application/json", false)][DataRow("application/xml", false)]
    [DataRow("application/octet-stream", false)]
    [DataRow("text/html; charset=\"", false)]
    [DataRow("text/html garbage", false)]
    public void OnlyExplicitValidHtmlOrXhtmlRepresentationContributes(string? contentType, bool expected)
    {
        var observation = Observation("http://child.i2p/");
        observation = observation with
        {
            HomepageResponse = observation.HomepageResponse! with { ContentType = contentType },
            HomepageMetadata = observation.HomepageMetadata! with { ContentType = contentType }
        };
        var result = Select(Snapshot(Parent), [Evidence(Parent, observation)]);
        Assert.HasCount(expected ? 1 : 0, result.WorkItems);
        Assert.AreEqual(expected ? 1L : 0L, result.EligibleUnknownReferenceCount);
    }

    [TestMethod]
    [DataRow("no-attempt")][DataRow("failed-outcome")][DataRow("no-response")][DataRow("no-metadata")]
    [DataRow("truncated")][DataRow("incomplete")][DataRow("null-response-type")][DataRow("null-metadata-type")]
    [DataRow("mismatched-types")][DataRow("oversized-type")]
    public void EveryHomepageEligibilityConditionIsRequired(string failure)
    {
        var original = Observation("http://child.i2p/");
        var changed = failure switch
        {
            "no-attempt" => original with { HomepageRequestOutcome = null },
            "failed-outcome" => original with { HomepageRequestOutcome = I2pRequestOutcome.TimedOut },
            "no-response" => original with { HomepageResponse = null },
            "no-metadata" => original with { HomepageMetadata = null },
            "truncated" => original with { HomepageMetadata = original.HomepageMetadata! with { BodyTruncated = true } },
            "incomplete" => original with { HomepageMetadata = original.HomepageMetadata! with { ExtractionIncomplete = true } },
            "null-response-type" => original with { HomepageResponse = original.HomepageResponse! with { ContentType = null } },
            "null-metadata-type" => original with { HomepageMetadata = original.HomepageMetadata! with { ContentType = null } },
            "mismatched-types" => original with { HomepageMetadata = original.HomepageMetadata! with { ContentType = "application/xhtml+xml" } },
            _ => original with { HomepageResponse = original.HomepageResponse! with { ContentType = "text/html; note=" + new string('x', 256) } }
        };
        var result = Select(Snapshot(Parent), [Evidence(Parent, changed)]);
        Assert.IsEmpty(result.WorkItems);
        Assert.AreEqual(0L, result.EligibleUnknownReferenceCount);
        Assert.IsFalse(result.AdmissionLimitApplied);
    }

    [TestMethod]
    [DataRow(201)][DataRow(204)][DataRow(206)][DataRow(301)][DataRow(302)]
    [DataRow(403)][DataRow(404)][DataRow(500)][DataRow(502)][DataRow(503)][DataRow(504)]
    public void Non200AndProxyErrorPagesCannotContributeEvenWithConstructedHtmlMetadata(int status)
    {
        var observation = Observation("http://poisoned.i2p/");
        observation = observation with { HomepageResponse = observation.HomepageResponse! with { StatusCode = status } };
        Assert.IsEmpty(Select(Snapshot(Parent), [Evidence(Parent, observation)]).WorkItems);
    }

    [TestMethod]
    public void AmbiguousHomepageClassificationRemainsAnObservationNotAnAuthenticationGate()
    {
        var observation = Observation("http://child.i2p/");
        Assert.AreEqual(I2pProxyResponseClassification.Ambiguous, observation.HomepageProxyResponseClassification);
        var result = Select(Snapshot(Parent), [Evidence(Parent, observation)]);
        Assert.HasCount(1, result.WorkItems);
        Assert.AreEqual(I2pOriginEvidence.Unproven, observation.HomepageOriginEvidence);
        Assert.AreEqual(I2pProxyResponseClassification.Ambiguous, observation.HomepageProxyResponseClassification);
    }

    [TestMethod]
    [DataRow("http://foo.i2p/a/b?q=1#fragment", "foo.i2p")]
    [DataRow("http://FOO.I2P.:80/x", "foo.i2p")]
    [DataRow("HTTP://sub.foo.i2p/", "sub.foo.i2p")]
    [DataRow("http://foo.i2p?query=ordinary", "foo.i2p")]
    [DataRow("http://foo.i2p/path%20segment", "foo.i2p")]
    public void ValidAbsoluteHttpUrlsContributeOnlyTheirHost(string url, string hostname)
    {
        var result = Select(Snapshot(Parent), [Evidence(Parent, Observation(url))]);
        Assert.AreEqual(hostname, result.WorkItems.Single().Target.Hostname);
    }

    [TestMethod]
    [DataRow("https://foo.i2p/")][DataRow("http://foo.i2p:8080/")][DataRow("http://foo.i2p:443/")]
    [DataRow("ftp://foo.i2p/")][DataRow("//foo.i2p/")][DataRow("/path")][DataRow("not a URL")]
    [DataRow("http://user:secret@foo.i2p/")][DataRow("http://@foo.i2p/")]
    [DataRow("http://foo.i2p/?i2paddresshelper=secret")][DataRow("http://foo.i2p/?I2PADDRESSHELPER=secret")]
    [DataRow("http://foo.i2p/?x=1&%69%32%70addresshelper=secret")][DataRow("http://foo.i2p/?i2paddresshelper")]
    [DataRow("http://example.com/")][DataRow("http://foo.i2p.example.com/")]
    [DataRow("http://127.0.0.1/")][DataRow("http://[::1]/")][DataRow("http://localhost/")]
    [DataRow("http://foo_bar.i2p/")][DataRow("http://foo..i2p/")][DataRow("http://foo.i2p../")]
    [DataRow("http://-foo.i2p/")][DataRow("http://fóo.i2p/")][DataRow("http://fr%69endly.i2p/")]
    [DataRow("http://foo.i2p\\@example.com/")][DataRow("http://foo.i2p/path with space")]
    [DataRow("http://foo.i2p/\r\nHost: x")]
    [DataRow("http://proxy.i2p/")][DataRow("http://sub.proxy.i2p/")]
    [DataRow("http://router.i2p/")][DataRow("http://a.b.router.i2p/")]
    [DataRow("http://CONSOLE.I2P./")][DataRow("http://sub.console.i2p/")]
    [DataRow("http://b32.i2p/")][DataRow("http://abc.b32.i2p/")]
    public void IneligibleUrlsCannotEnterTheFrontier(string url)
    {
        var result = Select(Snapshot(Parent), [Evidence(Parent, Observation(url))]);
        Assert.IsEmpty(result.WorkItems);
        Assert.AreEqual(0L, result.EligibleUnknownReferenceCount);
    }

    [TestMethod]
    public void SharedClassicBase32ValidationIsUsedWithoutAliasInference()
    {
        var malformedBits = new string('a', 51) + "b.b32.i2p";
        var extended = new string('a', 56) + ".b32.i2p";
        var result = Select(Snapshot(Parent), [Evidence(Parent, Observation(
            $"http://{UnknownB32.ToUpperInvariant()}./path", $"http://{UnknownB32}/other", "http://friendly.i2p/",
            $"http://{malformedBits}/", $"http://{extended}/"))]);
        Assert.HasCount(2, result.WorkItems);
        var b32 = result.WorkItems.Single(w => w.Target.HostForm == I2pDiscoveredHostForm.ClassicBase32);
        Assert.AreEqual(UnknownB32, b32.Target.Hostname);
        Assert.AreEqual("friendly.i2p", result.WorkItems.Single(w => w.Target.HostForm == I2pDiscoveredHostForm.Friendly).Target.Hostname);
        Assert.AreEqual(3L, result.EligibleUnknownReferenceCount);
    }

    [TestMethod]
    public void ClearnetKindRemainsInertEvenIfTheObservedUrlLooksLikeI2p()
    {
        var observation = Observation("http://foo.i2p/");
        observation = observation with
        {
            HomepageMetadata = observation.HomepageMetadata! with
            {
                Links = [new("http://foo.i2p/", I2pDiscoveredLinkKind.Clearnet), new("https://example.com/", I2pDiscoveredLinkKind.Clearnet)]
            }
        };
        Assert.IsEmpty(Select(Snapshot(Parent), [Evidence(Parent, observation)]).WorkItems);
    }

    [TestMethod]
    public void FullSnapshotSuppressesUnselectedFriendlyAndB32NamesWithoutInventingEvidence()
    {
        var a = Seed("a.i2p", 1);
        var b = Seed("b.i2p", 2);
        var c = Seed("c.i2p", 3);
        var snapshot = Snapshot(a, b, c);
        var first = Evidence(a, Observation("http://B.I2P./", $"http://{B32(b)}/", $"http://{B32(c)}/", "http://unknown.i2p/"));
        var result = Select(snapshot, [first]);
        CollectionAssert.AreEqual(new[] { "unknown.i2p" }, Hosts(result));
        Assert.AreEqual(1L, result.EligibleUnknownReferenceCount);
        Assert.IsEmpty(Select(snapshot, []).WorkItems);
        var explicitSecond = Evidence(b, Observation("http://only-from-b.i2p/"));
        CollectionAssert.AreEqual(new[] { "only-from-b.i2p", "unknown.i2p" }, Hosts(Select(snapshot, [first, explicitSecond])));
    }

    [TestMethod]
    public void MultiDestinationParentsAreIndependentAndReferrersCompareByB32AfterHostname()
    {
        var a = Seed("plugins.i2p", 1);
        var b = Seed("plugins.i2p", 2);
        var parents = ImmutableArray.Create(
            Evidence(a, Observation("http://plugins.i2p/", $"http://{B32(a)}/", "http://only-a.i2p/", "http://shared.i2p/")),
            Evidence(b, Observation($"http://{B32(b)}/", "http://only-b.i2p/", "http://shared.i2p/")));
        var snapshot = Snapshot(a, b);
        var expectedParent = new[] { I2pCrawlReferrer.FromSeed(a), I2pCrawlReferrer.FromSeed(b) }.Min();
        foreach (var order in new[] { parents, parents.Reverse().ToImmutableArray() })
        {
            var result = Select(snapshot, order);
            CollectionAssert.AreEqual(new[] { "only-a.i2p", "only-b.i2p", "shared.i2p" }, Hosts(result));
            Assert.AreEqual(expectedParent, result.WorkItems.Single(w => w.Target.Hostname == "shared.i2p").FirstReferrer);
        }
    }

    [TestMethod]
    public void SeedMembershipUsesSemanticIdentityRatherThanPreservedPaddingSpelling()
    {
        var padded = new I2pSeed("parent.i2p", SeedSnapshotTestData.Destination(payloadLength: 4));
        var unpadded = new I2pSeed("PARENT.I2P.", padded.Destination.TrimEnd('='));
        var result = Select(Snapshot(padded), [Evidence(unpadded, Observation("http://child.i2p/"))]);
        Assert.AreEqual(I2pCrawlReferrer.FromSeed(padded), result.WorkItems.Single().FirstReferrer);
    }

    [TestMethod]
    public void SelfLinksAndCycleShapedReferencesDoNotProduceDuplicateWork()
    {
        var a = Seed("a.i2p", 1);
        var b = Seed("b.i2p", 2);
        var result = Select(Snapshot(a, b), [
            Evidence(a, Observation("http://a.i2p/", $"http://{B32(a)}/", "http://b.i2p/", "http://child.i2p/a")),
            Evidence(b, Observation("http://a.i2p/", "http://b.i2p/", "http://CHILD.I2P./b?q=1"))]);
        Assert.AreEqual("child.i2p", result.WorkItems.Single().Target.Hostname);
        Assert.AreEqual(2L, result.EligibleUnknownReferenceCount);
        Assert.AreEqual(I2pCrawlReferrer.FromSeed(a), result.WorkItems.Single().FirstReferrer);
    }

    [TestMethod]
    [DataRow(0)][DataRow(1)][DataRow(37)][DataRow(1000)]
    public void HundredsOfDistinctLinksSelectTheGlobalSmallestKInEveryOrder(int maximum)
    {
        var seeds = Enumerable.Range(0, 5).Select(i => Seed($"parent{i}.i2p", i)).ToArray();
        var parents = seeds.Select((seed, index) => Evidence(seed, Observation(Enumerable.Range(index * 250, 250)
            .Select(i => $"http://h{1249 - i:D4}.i2p/PRIVATE_PATH?q={i}").ToArray()))).ToImmutableArray();
        var expected = Enumerable.Range(0, maximum).Select(i => $"h{i:D4}.i2p").ToArray();
        var snapshot = Snapshot(seeds);
        var baseline = Select(snapshot, parents, maximum);
        CollectionAssert.AreEqual(expected, Hosts(baseline));
        Assert.IsTrue(baseline.AdmissionLimitApplied);
        Assert.AreEqual(1250L, baseline.EligibleUnknownReferenceCount);
        var random = new Random(1234);
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var shuffled = parents.OrderBy(_ => random.Next()).Select(p => p with
            {
                Observation = p.Observation with { HomepageMetadata = p.Observation.HomepageMetadata! with
                { Links = p.Observation.HomepageMetadata.Links.OrderBy(_ => random.Next()).ToImmutableArray() } }
            }).ToImmutableArray();
            AssertSameFrontier(baseline, Select(snapshot, shuffled, maximum));
        }
    }

    [TestMethod]
    public void LaterSmallerCandidateReplacesLargestAndLaterSmallerParentUpdatesProvenance()
    {
        var z = Seed("z-parent.i2p", 1);
        var a = Seed("a-parent.i2p", 2);
        var parents = ImmutableArray.Create(Evidence(z, Observation("http://z.i2p/", "http://m.i2p/", "http://a.i2p/")),
            Evidence(a, Observation("http://m.i2p/", "http://a.i2p/")));
        var snapshot = Snapshot(a, z);
        var result = Select(snapshot, parents, 2);
        CollectionAssert.AreEqual(new[] { "a.i2p", "m.i2p" }, Hosts(result));
        Assert.IsTrue(result.WorkItems.All(w => w.FirstReferrer == I2pCrawlReferrer.FromSeed(a)));
        AssertSameFrontier(result, Select(snapshot, parents.Reverse().ToImmutableArray(), 2));
    }

    [TestMethod]
    public async Task SimulatedWorkerCompletionOrderDoesNotChooseFrontierOrFirstParent()
    {
        var seeds = new[] { Seed("z.i2p", 1), Seed("a.i2p", 2), Seed("m.i2p", 3) };
        var parents = seeds.Select(s => Evidence(s, Observation("http://common.i2p/", $"http://z-child-{s.Hostname}/"))).ToArray();
        var snapshot = Snapshot(seeds);
        I2pCrawlFrontier? baseline = null;
        foreach (var order in new[] { new[] { 0, 1, 2 }, new[] { 2, 1, 0 }, new[] { 1, 2, 0 } })
        {
            var completions = parents.Select(_ => new TaskCompletionSource<I2pCrawlSeedObservation>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var pending = completions.Select(t => t.Task).ToList();
            var completed = ImmutableArray.CreateBuilder<I2pCrawlSeedObservation>();
            foreach (var index in order)
            {
                completions[index].SetResult(parents[index]);
                var next = await Task.WhenAny(pending);
                completed.Add(await next);
                pending.Remove(next);
            }
            var result = Select(snapshot, completed.ToImmutable(), 3);
            if (baseline == null) baseline = result;
            else AssertSameFrontier(baseline, result);
            Assert.AreEqual("a.i2p", result.WorkItems.Single(w => w.Target.Hostname == "common.i2p").FirstReferrer.Hostname);
        }
    }

    [TestMethod]
    public void SelectionAndProvenanceAreCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        var seeds = new[] { Seed("z-parent.i2p", 1), Seed("i-parent.i2p", 2) };
        var parents = seeds.Select(s => Evidence(s, Observation("http://Z.I2P/", "http://I.I2P/"))).ToImmutableArray();
        var baseline = Select(Snapshot(seeds), parents, 1);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            AssertSameFrontier(baseline, Select(Snapshot(seeds), parents.Reverse().ToImmutableArray(), 1));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    public void DuplicateReferencesDoNotFalselySetAdmissionLimitAndZeroStillReportsReferences()
    {
        var parent = Evidence(Parent, Observation("http://foo.i2p/1", "http://FOO.I2P./2", "http://foo.i2p/3"));
        var snapshot = Snapshot(Parent);
        var one = Select(snapshot, [parent], 1);
        Assert.HasCount(1, one.WorkItems);
        Assert.AreEqual(3L, one.EligibleUnknownReferenceCount);
        Assert.IsFalse(one.AdmissionLimitApplied);
        var zero = Select(snapshot, [parent], 0);
        Assert.IsEmpty(zero.WorkItems);
        Assert.AreEqual(3L, zero.EligibleUnknownReferenceCount);
        Assert.IsTrue(zero.AdmissionLimitApplied);
        Assert.IsFalse(Select(snapshot, [Evidence(Parent, Observation("https://foo.i2p/"))], 0).AdmissionLimitApplied);
    }

    [TestMethod]
    public void SettingsAndResultsHaveInclusiveBoundsAndImmutableOutput()
    {
        Assert.AreEqual(100, new I2pCrawlDiscoverySettings().MaximumDiscoveredHosts);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pCrawlDiscoverySettings(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pCrawlDiscoverySettings(1001));
        var snapshot = Snapshot(Parent);
        var result = Select(snapshot, [Evidence(Parent, Observation("http://a.i2p/", "http://b.i2p/"))], 1000);
        var removedCopy = result.WorkItems.RemoveAt(0);
        Assert.HasCount(2, result.WorkItems);
        Assert.HasCount(1, removedCopy);
        var empty = Select(Snapshot(), []);
        Assert.IsEmpty(empty.WorkItems);
        Assert.AreEqual(0L, empty.EligibleUnknownReferenceCount);
        Assert.IsFalse(empty.AdmissionLimitApplied);
    }

    [TestMethod]
    public void ExcessiveOrAmbiguousConstructedInputsFailExplicitly()
    {
        var snapshot = Snapshot(Parent);
        var parent = Evidence(Parent, Observation("http://child.i2p/"));
        Assert.ThrowsExactly<ArgumentException>(() => Select(snapshot, [parent, parent]));
        Assert.ThrowsExactly<ArgumentException>(() => Select(snapshot, [Evidence(Seed("not-in-snapshot.i2p"), parent.Observation)]));
        var alternateSpelling = new I2pSeed("parent.i2p", SeedSnapshotTestData.Destination(0, 4));
        var alternate = Evidence(alternateSpelling, parent.Observation);
        var equivalent = Evidence(new I2pSeed("parent.i2p", alternateSpelling.Destination.TrimEnd('=')), parent.Observation);
        Assert.ThrowsExactly<ArgumentException>(() => Select(Snapshot(alternateSpelling), [alternate, equivalent]));
        Assert.ThrowsExactly<ArgumentException>(() => Select(snapshot, default));
        Assert.ThrowsExactly<ArgumentException>(() => Select(snapshot, Enumerable.Repeat(parent, 10001).ToImmutableArray()));
        var excess = Observation(Enumerable.Range(0, 251).Select(i => $"http://h{i}.i2p/").ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => Select(snapshot, [Evidence(Parent, excess)]));
        var uninitialized = parent.Observation with { HomepageMetadata = parent.Observation.HomepageMetadata! with { Links = default } };
        Assert.ThrowsExactly<ArgumentException>(() => Select(snapshot, [Evidence(Parent, uninitialized)]));
        var tooLong = Select(snapshot, [Evidence(Parent, Observation("http://foo.i2p/" + new string('x', 4096)))]);
        Assert.IsEmpty(tooLong.WorkItems);
    }

    [TestMethod]
    public void PureSelectionHonorsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.ThrowsExactly<OperationCanceledException>(() => I2pHostDiscovery.Select(Snapshot(Parent),
            [Evidence(Parent, Observation("http://child.i2p/"))], cancellationToken: cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
    }

    [TestMethod]
    public void DiscoveryCallGraphHasNoNetworkIoEnvironmentOrSchedulingDependencies()
    {
        // Inspect IL in memory, including same-assembly helpers and compiler-generated
        // lambdas. This catches a static DNS/file/environment call without needing a router.
        var assembly = typeof(I2pHostDiscovery).Assembly;
        var pending = new Stack<MethodBase>(typeof(I2pHostDiscovery).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        var visited = new HashSet<MethodBase>();
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => unchecked((ushort)o.Value));
        while (pending.TryPop(out var method))
        {
            if (!visited.Add(method) || method.GetMethodBody()?.GetILAsByteArray() is not { } il) continue;
            for (var offset = 0; offset < il.Length;)
            {
                var value = (ushort)il[offset++];
                if (value == 0xfe) value = (ushort)(0xfe00 | il[offset++]);
                var opcode = opcodes[value];
                Assert.AreNotEqual(OpCodes.Calli, opcode, "Indirect calls could hide an I/O dependency.");
                if (opcode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
                {
                    var member = method.Module.ResolveMember(BitConverter.ToInt32(il, offset),
                        method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null)!;
                    var type = member as Type ?? member.DeclaringType!;
                    var name = type.FullName ?? "";
                    Assert.IsFalse(name.StartsWith("System.IO.", StringComparison.Ordinal) ||
                        name.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
                        name.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal) ||
                        name == "System.Environment" ||
                        name.StartsWith("System.Net.", StringComparison.Ordinal) && !name.StartsWith("System.Net.Http.Headers.", StringComparison.Ordinal) ||
                        type == typeof(I2pHttpTransport) || type == typeof(I2pHttpProbe) || type == typeof(I2pProbeRequest),
                        $"Pure discovery references {name} through {method.Name}.");
                    if (member is MethodBase called && called.DeclaringType?.Assembly == assembly) pending.Push(called);
                }
                offset += opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                    _ => 4
                };
            }
        }
    }

    private static I2pSeed Seed(string hostname, int salt = 0) => new(hostname, SeedSnapshotTestData.Destination(salt));
    private static string B32(I2pSeed seed) => I2pSeedIdentity.FromSeed(seed).DestinationHostname;
    private static I2pSeedSnapshot Snapshot(params I2pSeed[] seeds) => new(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero),
        new("/DO-NOT-OPEN/private-hosts.txt", new string('A', 64), 0), seeds.OrderBy(s => s.Hostname, StringComparer.Ordinal)
            .ThenBy(s => s.Destination, StringComparer.Ordinal).ToImmutableArray());
    private static I2pCrawlSeedObservation Evidence(I2pSeed seed, I2pProbeObservation observation) => new(new(seed), observation);
    private static I2pCrawlFrontier Select(I2pSeedSnapshot snapshot, ImmutableArray<I2pCrawlSeedObservation> parents, int maximum = 100) =>
        I2pHostDiscovery.Select(snapshot, parents, new(maximum));
    private static string[] Hosts(I2pCrawlFrontier result) => result.WorkItems.Select(w => w.Target.Hostname).ToArray();
    private static void AssertSameFrontier(I2pCrawlFrontier expected, I2pCrawlFrontier actual)
    {
        CollectionAssert.AreEqual(Hosts(expected), Hosts(actual));
        CollectionAssert.AreEqual(expected.WorkItems.Select(w => w.FirstReferrer).ToArray(), actual.WorkItems.Select(w => w.FirstReferrer).ToArray());
        Assert.AreEqual(expected.EligibleUnknownReferenceCount, actual.EligibleUnknownReferenceCount);
        Assert.AreEqual(expected.AdmissionLimitApplied, actual.AdmissionLimitApplied);
    }
    private static I2pProbeObservation Observation(params string[] urls) => new(
        I2pRequestOutcome.ProxyHttpResponse, I2pOriginEvidence.Unproven, I2pProxyResponseClassification.LikelyOriginResponse,
        new(200, "text/plain", 20, null, false), I2pRobotsObservation.AllowsRoot, I2pHomepageDisposition.ResponseObserved,
        I2pRequestOutcome.ProxyHttpResponse, new(200, "text/html", 100, null, false),
        new("text/html", 100, false, "parent", urls.Select(u => new I2pDiscoveredLink(u, I2pDiscoveredLinkKind.I2p)).ToImmutableArray(), false),
        I2pProxyResponseClassification.Ambiguous);
}
