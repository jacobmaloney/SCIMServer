using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace SCIMServer.Core.Services
{
    public enum ScimProbeAuth
    {
        None,
        Bearer,
        Basic,
        OAuthClientCredentials
    }

    public enum ScimProbeOutcome
    {
        Passed,
        Warning,
        Failed,
        Skipped
    }

    /// <summary>
    /// What to test: any SCIM 2.0 base URL (for example https://host/scim/v2) plus the
    /// credential the remote system expects.
    /// </summary>
    public class ScimProbeRequest
    {
        public string BaseUrl { get; set; } = string.Empty;
        public ScimProbeAuth Auth { get; set; } = ScimProbeAuth.Bearer;
        public string Token { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string TokenUrl { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;

        /// <summary>Longest time any single step may take before it is reported as timed out.</summary>
        public int TimeoutSeconds { get; set; } = 20;

        /// <summary>Carry on past an untrusted certificate (reported as a warning) so the later steps still run.</summary>
        public bool IgnoreCertificateErrors { get; set; }

        /// <summary>Send the HTTP requests through the machine's configured proxy, as most products do.</summary>
        public bool UseSystemProxy { get; set; } = true;
    }

    public class ScimProbeStep
    {
        public string Name { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public ScimProbeOutcome Outcome { get; set; } = ScimProbeOutcome.Passed;
        public long ElapsedMs { get; set; }
        public int? StatusCode { get; set; }
        public List<string> Details { get; } = new();
        public string? Body { get; set; }
    }

    /// <summary>
    /// Tests a connection to someone else's SCIM endpoint one layer at a time (name lookup,
    /// TCP, TLS, sign-in, then the SCIM discovery and list calls) and times each layer, so a
    /// "cannot connect" report can be pinned to the layer that actually fails or stalls.
    /// Secrets are sent to the target but never written into a step's details or body.
    /// </summary>
    public class ScimConnectionProbe
    {
        private const int MaxBodyCharacters = 6000;
        private const int DiscoveryCallCount = 3; // ServiceProviderConfig, Schemas, ResourceTypes

        public async Task<List<ScimProbeStep>> RunAsync(
            ScimProbeRequest request,
            Func<string, Task>? onStepStarted = null,
            Func<ScimProbeStep, Task>? onStepFinished = null,
            CancellationToken ct = default)
        {
            var steps = new List<ScimProbeStep>();
            var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 600));

            async Task<ScimProbeStep> Run(string name, string target, string timeoutHint,
                Func<ScimProbeStep, CancellationToken, Task> body)
            {
                if (onStepStarted != null) await onStepStarted(name);
                var step = await RunStepAsync(name, target, timeout, timeoutHint, ct, body);
                steps.Add(step);
                if (onStepFinished != null) await onStepFinished(step);
                return step;
            }

            async Task Skip(string reason, params string[] names)
            {
                foreach (var name in names)
                {
                    var step = new ScimProbeStep { Name = name, Outcome = ScimProbeOutcome.Skipped };
                    step.Details.Add(reason);
                    steps.Add(step);
                    if (onStepFinished != null) await onStepFinished(step);
                }
            }

            string[] httpStepNames = request.Auth == ScimProbeAuth.OAuthClientCredentials
                ? new[] { "Get access token", "ServiceProviderConfig", "Schemas", "ResourceTypes", "List users" }
                : new[] { "ServiceProviderConfig", "Schemas", "ResourceTypes", "List users" };

            // ---- 1. The address itself -------------------------------------------------
            Uri? baseUri = null;
            var parse = await Run("Read the address", request.BaseUrl, "", (step, _) =>
            {
                var text = (request.BaseUrl ?? string.Empty).Trim();
                if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
                    (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                {
                    step.Outcome = ScimProbeOutcome.Failed;
                    step.Details.Add("This is not a full http:// or https:// address. Enter the SCIM base URL, for example https://host/scim/v2.");
                    return Task.CompletedTask;
                }
                baseUri = parsed;
                step.Details.Add($"Scheme {parsed.Scheme}, host {parsed.Host}, port {parsed.Port}, path {parsed.AbsolutePath}");
                if (parsed.Scheme == Uri.UriSchemeHttp)
                {
                    step.Outcome = ScimProbeOutcome.Warning;
                    step.Details.Add("Plain http: the credential travels unencrypted.");
                }
                return Task.CompletedTask;
            });
            if (parse.Outcome == ScimProbeOutcome.Failed || baseUri == null)
            {
                await Skip("Not run: the address could not be read.",
                    new[] { "Name lookup", "TCP connect", "TLS handshake" }.Concat(httpStepNames).ToArray());
                return steps;
            }

            // ---- 2. Is a proxy in the way? ---------------------------------------------
            Uri? proxy = null;
            if (request.UseSystemProxy)
            {
                try
                {
                    var systemProxy = HttpClient.DefaultProxy;
                    if (!systemProxy.IsBypassed(baseUri))
                    {
                        var candidate = systemProxy.GetProxy(baseUri);
                        if (candidate != null && candidate != baseUri) proxy = candidate;
                    }
                }
                catch
                {
                    // Reading the proxy configuration is best-effort.
                }
            }
            var viaProxy = proxy != null;

            // ---- 3. Name lookup --------------------------------------------------------
            var networkOk = true;
            var dns = await Run("Name lookup", baseUri.Host,
                "The name lookup did not answer. Check the DNS servers this machine uses.",
                async (step, token) =>
                {
                    if (IPAddress.TryParse(baseUri.Host, out _))
                    {
                        step.Details.Add("The address is already an IP address; nothing to look up.");
                        return;
                    }
                    var addresses = await Dns.GetHostAddressesAsync(baseUri.Host, token);
                    if (addresses.Length == 0)
                    {
                        step.Outcome = ScimProbeOutcome.Failed;
                        step.Details.Add("The name exists but has no addresses.");
                        return;
                    }
                    step.Details.Add("Resolves to " + string.Join(", ", addresses.Select(a => a.ToString())));
                });
            if (dns.Outcome == ScimProbeOutcome.Failed) networkOk = false;

            // ---- 4. TCP connect + 5. TLS handshake (direct, not through the proxy) ------
            if (!networkOk)
            {
                await Skip("Not run: the name lookup failed.", "TCP connect", "TLS handshake");
            }
            else
            {
                TcpClient? tcp = null;
                try
                {
                    var connect = await Run("TCP connect", $"{baseUri.Host}:{baseUri.Port}",
                        $"Nothing answered on port {baseUri.Port}. A connection that hangs instead of being refused usually means a firewall is silently dropping the packets, or the host is down.",
                        async (step, token) =>
                        {
                            tcp = new TcpClient();
                            await tcp.ConnectAsync(baseUri.Host, baseUri.Port, token);
                            step.Details.Add($"Connected to {tcp.Client.RemoteEndPoint}");
                        });
                    if (connect.Outcome == ScimProbeOutcome.Failed) networkOk = false;

                    if (baseUri.Scheme != Uri.UriSchemeHttps)
                    {
                        await Skip("Not run: the address is http, so there is no TLS.", "TLS handshake");
                    }
                    else if (!networkOk || tcp == null)
                    {
                        await Skip("Not run: the TCP connection failed.", "TLS handshake");
                    }
                    else
                    {
                        var tls = await Run("TLS handshake", baseUri.Host,
                            "The server accepted the connection but did not complete the TLS handshake. Something between here and the server may be intercepting or the port is not a TLS port.",
                            async (step, token) =>
                            {
                                var policyErrors = SslPolicyErrors.None;
                                X509Certificate2? serverCert = null;
                                var chainProblems = new List<string>();

                                using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                                {
                                    TargetHost = baseUri.Host,
                                    RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                                    {
                                        policyErrors = errors;
                                        if (cert != null) serverCert = new X509Certificate2(cert);
                                        if (chain != null)
                                        {
                                            chainProblems.AddRange(chain.ChainStatus
                                                .Select(s => s.StatusInformation.Trim())
                                                .Where(s => s.Length > 0));
                                        }
                                        return true; // always finish the handshake so the certificate can be reported
                                    }
                                }, token);

                                step.Details.Add($"Protocol {ssl.SslProtocol}");
                                if (serverCert != null)
                                {
                                    step.Details.Add($"Certificate subject: {serverCert.Subject}");
                                    step.Details.Add($"Issued by: {serverCert.Issuer}");
                                    step.Details.Add($"Valid {serverCert.NotBefore:yyyy-MM-dd} to {serverCert.NotAfter:yyyy-MM-dd}");
                                }

                                if (policyErrors != SslPolicyErrors.None)
                                {
                                    step.Outcome = request.IgnoreCertificateErrors
                                        ? ScimProbeOutcome.Warning
                                        : ScimProbeOutcome.Failed;
                                    step.Details.Add($"This machine does not trust the certificate: {policyErrors}.");
                                    foreach (var problem in chainProblems.Distinct()) step.Details.Add(problem);
                                    step.Details.Add(request.IgnoreCertificateErrors
                                        ? "Carrying on because \"Ignore certificate errors\" is on. A product that checks certificates will refuse this connection."
                                        : "A product that checks certificates will refuse this connection. Turn on \"Ignore certificate errors\" to test the later steps anyway.");
                                }
                            });
                        if (tls.Outcome == ScimProbeOutcome.Failed) networkOk = false;
                    }
                }
                finally
                {
                    tcp?.Dispose();
                }
            }

            if (!networkOk && !viaProxy)
            {
                await Skip("Not run: the network connection to the server failed, so no request can reach it.", httpStepNames);
                return steps;
            }
            if (ct.IsCancellationRequested)
            {
                await Skip("Not run: the test was cancelled.", httpStepNames);
                return steps;
            }

            // ---- 6+. HTTP: sign-in and the SCIM calls ----------------------------------
            using var handler = new SocketsHttpHandler
            {
                UseProxy = request.UseSystemProxy,
                AllowAutoRedirect = false, // a redirect is a finding (usually a sign-in page), so report it
                ConnectTimeout = timeout
            };
            if (request.IgnoreCertificateErrors)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            var proxyNote = viaProxy
                ? $"Sent through the system proxy {proxy}. The name lookup, TCP and TLS steps above tested the direct path and do not describe the proxy path."
                : null;

            string? authorization = null;
            switch (request.Auth)
            {
                case ScimProbeAuth.Bearer:
                    authorization = "Bearer " + (request.Token ?? string.Empty).Trim();
                    break;
                case ScimProbeAuth.Basic:
                    authorization = "Basic " + Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{request.Username}:{request.Password}"));
                    break;
                case ScimProbeAuth.OAuthClientCredentials:
                    var tokenStep = await Run("Get access token", "POST " + request.TokenUrl,
                        "The token endpoint did not answer.",
                        async (step, token) =>
                        {
                            if (!Uri.TryCreate((request.TokenUrl ?? string.Empty).Trim(), UriKind.Absolute, out var tokenUri))
                            {
                                step.Outcome = ScimProbeOutcome.Failed;
                                step.Details.Add("The token URL is not a full http:// or https:// address.");
                                return;
                            }
                            var form = new Dictionary<string, string>
                            {
                                ["grant_type"] = "client_credentials",
                                ["client_id"] = request.ClientId ?? string.Empty,
                                ["client_secret"] = request.ClientSecret ?? string.Empty
                            };
                            if (!string.IsNullOrWhiteSpace(request.Scope)) form["scope"] = request.Scope.Trim();

                            using var message = new HttpRequestMessage(HttpMethod.Post, tokenUri)
                            {
                                Content = new FormUrlEncodedContent(form)
                            };
                            message.Headers.TryAddWithoutValidation("Accept", "application/json");
                            using var response = await http.SendAsync(message, token);
                            var text = await response.Content.ReadAsStringAsync(token);
                            step.StatusCode = (int)response.StatusCode;
                            step.Details.Add($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

                            if (!response.IsSuccessStatusCode)
                            {
                                step.Outcome = ScimProbeOutcome.Failed;
                                step.Details.Add("The token endpoint refused the client id and secret. Its answer is shown below.");
                                step.Body = Truncate(Pretty(text));
                                return;
                            }

                            string? accessToken = null;
                            try
                            {
                                using var doc = JsonDocument.Parse(text);
                                if (TryGet(doc.RootElement, "access_token", out var at)) accessToken = at.GetString();
                                if (TryGet(doc.RootElement, "token_type", out var tt)) step.Details.Add($"Token type: {tt}");
                                if (TryGet(doc.RootElement, "expires_in", out var ei)) step.Details.Add($"Expires in: {ei} seconds");
                            }
                            catch (JsonException)
                            {
                                // handled below: accessToken stays null
                            }

                            if (string.IsNullOrEmpty(accessToken))
                            {
                                step.Outcome = ScimProbeOutcome.Failed;
                                step.Details.Add("The answer was successful but contained no access_token.");
                                return;
                            }
                            // The response body is deliberately not kept: it contains the token.
                            step.Details.Add($"Access token received ({accessToken.Length} characters).");
                            authorization = "Bearer " + accessToken;
                        });
                    if (tokenStep.Outcome == ScimProbeOutcome.Failed)
                    {
                        await Skip("Not run: no access token was obtained.", httpStepNames.Skip(1).ToArray());
                        return steps;
                    }
                    break;
            }

            var root = baseUri.ToString().TrimEnd('/');
            // What the server says it holds. Not every SCIM system has /Users (an HR source may
            // expose Employees, Positions, ... instead), so the list calls below are taken from
            // the server's own ResourceTypes answer and only fall back to /Users without one.
            var resourceTypes = new List<(string Name, string Endpoint)>();
            var firstPages = new List<PageInfo>();
            var pagingQueued = false;

            var calls = new List<(string Name, string Path, Func<JsonElement, IEnumerable<string>> Summarize)>
            {
                ("ServiceProviderConfig", "/ServiceProviderConfig", SummarizeServiceProviderConfig),
                ("Schemas", "/Schemas", e => SummarizeList(e, "schema", "id")),
                ("ResourceTypes", "/ResourceTypes", e =>
                {
                    resourceTypes.AddRange(ReadResourceTypes(e));
                    return SummarizeList(e, "resource type", "name");
                })
            };

            for (var index = 0; index < calls.Count; index++)
            {
                var call = calls[index];

                if (ct.IsCancellationRequested)
                {
                    await Skip("Not run: the test was cancelled.", call.Name);
                    continue;
                }

                var url = root + call.Path;
                await Run(call.Name, "GET " + url,
                    "The server accepted the request but sent no answer in time. The server itself, or something it depends on, is slow or stuck.",
                    async (step, token) =>
                    {
                        if (proxyNote != null) step.Details.Add(proxyNote);

                        using var message = new HttpRequestMessage(HttpMethod.Get, url);
                        if (authorization != null) message.Headers.TryAddWithoutValidation("Authorization", authorization);
                        message.Headers.TryAddWithoutValidation("Accept", "application/scim+json, application/json");

                        var clock = Stopwatch.StartNew();
                        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                        var headersMs = clock.ElapsedMilliseconds;
                        var text = await response.Content.ReadAsStringAsync(token);

                        var code = (int)response.StatusCode;
                        step.StatusCode = code;
                        step.Details.Add($"HTTP {code} {response.ReasonPhrase}; first byte after {headersMs} ms; {text.Length:N0} characters; content type {response.Content.Headers.ContentType?.ToString() ?? "(none)"}");
                        if (response.Headers.Location != null) step.Details.Add($"Redirects to: {response.Headers.Location}");
                        if (response.Headers.WwwAuthenticate.Count > 0) step.Details.Add($"Server asks for: {string.Join(", ", response.Headers.WwwAuthenticate.Select(h => h.ToString()))}");
                        if (response.Headers.RetryAfter != null) step.Details.Add($"Retry-After: {response.Headers.RetryAfter}");

                        step.Body = Truncate(Pretty(text));

                        if (!response.IsSuccessStatusCode)
                        {
                            step.Outcome = ScimProbeOutcome.Failed;
                            step.Details.Add(StatusHint(code, request.Auth));
                            return;
                        }

                        try
                        {
                            using var doc = JsonDocument.Parse(text);
                            foreach (var line in call.Summarize(doc.RootElement)) step.Details.Add(line);
                        }
                        catch (JsonException)
                        {
                            step.Outcome = ScimProbeOutcome.Warning;
                            step.Details.Add("The answer is not JSON. A SCIM endpoint answers in JSON, so this address is probably a web page (a sign-in page, or the wrong base path).");
                        }
                    });

                // The three discovery calls are done: queue one list call per resource type.
                if (index == DiscoveryCallCount - 1)
                {
                    if (resourceTypes.Count == 0) resourceTypes.Add(("Users", "/Users"));
                    foreach (var type in resourceTypes)
                    {
                        var listed = type;
                        calls.Add(($"List {listed.Name}", WithQuery(listed.Endpoint, "startIndex=1&count=1"), e =>
                        {
                            firstPages.Add(ReadPage(listed.Name, listed.Endpoint, e));
                            return SummarizeResources(e).ToList();
                        }));
                    }
                }

                // All list calls are done: ask for a second page of the first type that returned
                // records, both ways a client might page, to see which one the server honours.
                if (!pagingQueued && index >= DiscoveryCallCount && index == calls.Count - 1)
                {
                    pagingQueued = true;
                    var sample = firstPages.FirstOrDefault(p => p.Count > 0 && p.FirstId != null);
                    if (sample != null)
                    {
                        calls.Add(($"Page 2 by startIndex ({sample.Name})",
                            WithQuery(sample.Endpoint, $"startIndex={sample.Count + 1}&count=1"),
                            e => ComparePages(sample, ReadPage(sample.Name, sample.Endpoint, e), byCursor: false).ToList()));
                        if (!string.IsNullOrEmpty(sample.NextCursor))
                        {
                            calls.Add(($"Page 2 by cursor ({sample.Name})",
                                WithQuery(sample.Endpoint, $"cursor={Uri.EscapeDataString(sample.NextCursor)}&count=1"),
                                e => ComparePages(sample, ReadPage(sample.Name, sample.Endpoint, e), byCursor: true).ToList()));
                        }
                    }
                }
            }

            return steps;
        }

        private static async Task<ScimProbeStep> RunStepAsync(string name, string target, TimeSpan timeout,
            string timeoutHint, CancellationToken ct, Func<ScimProbeStep, CancellationToken, Task> body)
        {
            var step = new ScimProbeStep { Name = name, Target = target };
            var clock = Stopwatch.StartNew();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await body(step, limit.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                step.Outcome = ScimProbeOutcome.Failed;
                step.Details.Add("Cancelled before it finished.");
            }
            catch (OperationCanceledException)
            {
                step.Outcome = ScimProbeOutcome.Failed;
                step.Details.Add($"Timed out: no result within {timeout.TotalSeconds:0} seconds.");
                if (!string.IsNullOrEmpty(timeoutHint)) step.Details.Add(timeoutHint);
            }
            catch (Exception ex)
            {
                step.Outcome = ScimProbeOutcome.Failed;
                foreach (var line in Describe(ex)) step.Details.Add(line);
            }
            step.ElapsedMs = clock.ElapsedMilliseconds;
            return step;
        }

        /// <summary>The exception and its causes, innermost detail included, without stack traces.</summary>
        private static IEnumerable<string> Describe(Exception ex)
        {
            var seen = new HashSet<string>();
            for (Exception? current = ex; current != null; current = current.InnerException)
            {
                var text = current is SocketException socket
                    ? $"{socket.SocketErrorCode}: {socket.Message}"
                    : $"{current.GetType().Name}: {current.Message}";
                if (seen.Add(current.Message)) yield return text;
            }
        }

        private static string StatusHint(int code, ScimProbeAuth auth) => code switch
        {
            301 or 302 or 303 or 307 or 308 =>
                "The server redirected instead of answering. SCIM endpoints do not redirect; this is usually a sign-in page or the wrong base path (check http vs https and any missing path segment).",
            400 => "The server rejected the request as malformed. Its answer below usually says why.",
            401 => auth == ScimProbeAuth.None
                ? "The server needs a credential and none was sent."
                : "The server reached the sign-in check and rejected the credential. The network path is fine; the key, user name or password is wrong, expired or revoked.",
            403 => "The credential was recognised but is not allowed to read this. Check the permissions or scope granted to it.",
            404 => "Nothing lives at this path. The base URL is probably wrong: compare it with the provider's documented SCIM base path.",
            405 => "The server does not allow GET here. Some providers do not implement this endpoint.",
            429 => "The server is rate limiting. Wait and try again.",
            501 => "The server says it does not implement this. The sign-in worked; this system simply does not offer this kind of record or operation.",
            >= 500 => "The request reached the server and the server failed. This is a fault on the provider's side; its answer below may say more.",
            _ => "The server answered with an unexpected status."
        };

        private static IEnumerable<string> SummarizeServiceProviderConfig(JsonElement root)
        {
            foreach (var feature in new[] { "patch", "bulk", "filter", "changePassword", "sort", "etag" })
            {
                if (TryGet(root, feature, out var node) && node.ValueKind == JsonValueKind.Object &&
                    TryGet(node, "supported", out var supported))
                {
                    var extra = feature == "filter" && TryGet(node, "maxResults", out var max) ? $" (max results {max})" : "";
                    yield return $"{feature}: {(supported.ValueKind == JsonValueKind.True ? "supported" : "not supported")}{extra}";
                }
            }
            if (TryGet(root, "authenticationSchemes", out var schemes) && schemes.ValueKind == JsonValueKind.Array)
            {
                var names = schemes.EnumerateArray()
                    .Select(s => TryGet(s, "type", out var t) ? t.ToString() : (TryGet(s, "name", out var n) ? n.ToString() : "?"));
                yield return "Sign-in methods the server advertises: " + string.Join(", ", names);
            }
        }

        private static IEnumerable<string> SummarizeList(JsonElement root, string noun, string labelProperty)
        {
            JsonElement items;
            if (root.ValueKind == JsonValueKind.Array) items = root;
            else if (!TryGet(root, "Resources", out items) || items.ValueKind != JsonValueKind.Array)
            {
                yield return "The answer is JSON but has no Resources list.";
                yield break;
            }

            var labels = items.EnumerateArray()
                .Select(i => TryGet(i, labelProperty, out var label) ? label.ToString() : "?")
                .ToList();
            yield return $"{labels.Count} {noun}{(labels.Count == 1 ? "" : "s")} returned";
            foreach (var label in labels.Take(25)) yield return "  " + label;
            if (labels.Count > 25) yield return $"  ... and {labels.Count - 25} more";
        }

        /// <summary>Name and endpoint of each resource type the server lists (list response or bare array).</summary>
        private static IEnumerable<(string Name, string Endpoint)> ReadResourceTypes(JsonElement root)
        {
            JsonElement items;
            if (root.ValueKind == JsonValueKind.Array) items = root;
            else if (!TryGet(root, "Resources", out items) || items.ValueKind != JsonValueKind.Array) yield break;

            foreach (var item in items.EnumerateArray())
            {
                if (!TryGet(item, "endpoint", out var endpointNode)) continue;
                var endpoint = (endpointNode.GetString() ?? string.Empty).Trim();
                if (endpoint.Length == 0) continue;

                // Some servers publish the full address; keep only the part after the base.
                if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute) &&
                    (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                {
                    endpoint = "/" + absolute.Segments.Last().Trim('/');
                }
                if (!endpoint.StartsWith('/')) endpoint = "/" + endpoint;

                var name = TryGet(item, "name", out var nameNode) ? nameNode.ToString() : endpoint.TrimStart('/');
                yield return (name, endpoint);
            }
        }

        private sealed record PageInfo(string Name, string Endpoint, int Count, string? FirstId, string? NextCursor);

        private static string WithQuery(string endpoint, string query) =>
            endpoint + (endpoint.Contains('?') ? "&" : "?") + query;

        private static PageInfo ReadPage(string name, string endpoint, JsonElement root)
        {
            var count = 0;
            string? firstId = null;
            if (TryGet(root, "Resources", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                count = items.GetArrayLength();
                var first = items.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && TryGet(first, "id", out var id)) firstId = id.ToString();
            }
            var nextCursor = TryGet(root, "nextCursor", out var cursor) ? cursor.ToString() : null;
            return new PageInfo(name, endpoint, count, firstId, nextCursor);
        }

        private static IEnumerable<string> ComparePages(PageInfo first, PageInfo second, bool byCursor)
        {
            var method = byCursor ? "the cursor" : "startIndex";
            yield return $"Items in this answer: {second.Count}";
            if (second.Count == 0 || second.FirstId == null)
            {
                yield return $"Nothing came back when paging by {method}: either there is no second page, or the server does not page this way.";
            }
            else if (second.FirstId == first.FirstId)
            {
                yield return $"The server ignored {method} and sent the first page again (same first record).";
                yield return $"A client that pages by {method} will receive the same {first.Count} records over and over and never reach the end.";
            }
            else
            {
                yield return $"A different page came back, so paging by {method} works on this server.";
                if (!string.IsNullOrEmpty(second.NextCursor)) yield return $"Cursor for the page after this one: {second.NextCursor}";
                else if (byCursor) yield return "No further cursor: this was the last page.";
            }
        }

        private static IEnumerable<string> SummarizeResources(JsonElement root)
        {
            if (TryGet(root, "totalResults", out var total)) yield return $"Total the server reports: {total}";
            else yield return "The server does not report a total (no totalResults), so a client cannot know in advance how many records exist.";
            if (TryGet(root, "nextCursor", out var nextCursor)) yield return $"The server pages with a cursor (nextCursor: {nextCursor}).";
            if (TryGet(root, "Resources", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                yield return items.GetArrayLength() > 1
                    ? $"Items in this answer: {items.GetArrayLength()}. One was asked for, so the server ignored the requested page size."
                    : $"Items in this answer: {items.GetArrayLength()} (one was asked for)";
                var first = items.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    var label = new[] { "userName", "displayName", "name", "id" }
                        .Select(key => TryGet(first, key, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null)
                        .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "(no name or id)";
                    yield return $"First item returned: {label}";
                    yield return "Attributes on that item: " + string.Join(", ", first.EnumerateObject().Select(p => p.Name));
                }
                else
                {
                    yield return "The list came back empty. The credential works, but it can see nothing of this type.";
                }
            }
            else
            {
                yield return "The answer is JSON but has no Resources list.";
            }
        }

        /// <summary>SCIM attribute names are case-insensitive, so look them up that way.</summary>
        private static bool TryGet(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }
            value = default;
            return false;
        }

        private static string Pretty(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                return text;
            }
        }

        private static string Truncate(string text) =>
            text.Length <= MaxBodyCharacters
                ? text
                : text.Substring(0, MaxBodyCharacters) + $"\n... (cut; {text.Length:N0} characters in total)";
    }
}
