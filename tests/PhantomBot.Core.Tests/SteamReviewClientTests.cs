using System.Globalization;
using System.Net;
using System.Text.Json;
using PhantomBot.Core.Domain;
using PhantomBot.Core.Exceptions;
using PhantomBot.Infrastructure.Steam;

namespace PhantomBot.Core.Tests;

// ReSharper disable once MemberCanBeFileLocal
public sealed class SteamReviewClientTests{
    private const ulong UserId = 76_561_198_297_114_542UL;
    private const string UserUrl = "https://steamcommunity.com/profiles/76561198297114542/";
    private const string CuratorUrl = "https://store.steampowered.com/curator/1850/";

    [Theory]
    [InlineData("76561198297114542", UserUrl)]
    [InlineData(UserUrl, UserUrl)]
    [InlineData(UserUrl + "recommended/570/", UserUrl)]
    [InlineData("https://steamcommunity.com/id/BlightStone/", "https://steamcommunity.com/id/BlightStone/")]
    [InlineData("https://steamcommunity.com/id/BlightStone/recommended/570/", "https://steamcommunity.com/id/BlightStone/")]
    public async Task ResolveSourceCanonicalizesUserIdentity(string input, string requestedProfileUrl){
        using var test = new TestClient();

        test.Queue(requestedProfileUrl + "?l=english", CreateProfileHtml());

        var source = await test.Client.ResolveSourceAsync(input, CancellationToken.None);

        Assert.Equal(SteamReviewSourceKind.User, source.Kind);
        Assert.Equal(UserId, source.Id);
        Assert.Equal("Test Reviewer", source.Name);
        Assert.Equal(UserUrl, source.Url);
        Assert.Single(test.Requests);
    }

    [Theory]
    [InlineData("1850")]
    [InlineData("https://store.steampowered.com/curator/1850-PC-Gamer/")]
    [InlineData("https://store.steampowered.com/app/1368140/Corsair_Cove/?curator_clanid=1850")]
    public async Task ResolveSourceCanonicalizesCuratorIdentity(string input){
        using var test = new TestClient();

        test.Queue(CuratorUrl + "?l=english", CreateCuratorProfileHtml());

        var source = await test.Client.ResolveSourceAsync(input, CancellationToken.None);

        Assert.Equal(SteamReviewSourceKind.Curator, source.Kind);
        Assert.Equal(1_850UL, source.Id);
        Assert.Equal("PC Gamer", source.Name);
        Assert.Equal(CuratorUrl, source.Url);
        Assert.Single(test.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("https://steamcommunity.com.example.com/id/BlightStone/")]
    [InlineData("https://steamcommunity.com:444/id/BlightStone/")]
    [InlineData("ftp://steamcommunity.com/id/BlightStone/")]
    [InlineData("https://store.steampowered.com/app/570/")]
    [InlineData("4294967296")]
    [InlineData("https://user:password@steamcommunity.com/id/BlightStone/")]
    [InlineData("https://steamcommunity.com/id/Blight%2FStone/")]
    [InlineData("https://steamcommunity.com/id/Blight%5CStone/")]
    public async Task ResolveSourceRejectsUnsupportedInputWithoutMakingRequest(string input){
        using var test = new TestClient();

        var exception = await Assert.ThrowsAsync<SteamReviewInputException>(() => test.Client.ResolveSourceAsync(input, CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        Assert.Empty(test.Requests);
    }

    [Theory]
    [InlineData(SteamReviewSourceKind.User)]
    [InlineData(SteamReviewSourceKind.Curator)]
    public async Task ResolveSourceRejectsMismatchedIdentity(SteamReviewSourceKind kind){
        using var test = new TestClient();

        var source = CreateSource(kind);
        var body = kind == SteamReviewSourceKind.User ? CreateProfileHtml(UserId + 1) : CreateCuratorProfileHtml(1_851);

        test.Queue(source.Url + "?l=english", body);

        await Assert.ThrowsAsync<InvalidDataException>(() => test.Client.ResolveSourceAsync(source.Url, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveSourceRejectsPrivateProfile(){
        using var test = new TestClient();

        test.Queue(UserUrl + "?l=english",
            """
            <html>
              <body>
                <div class="profile_private_info">This profile is private.</div>
              </body>
            </html>
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => test.Client.ResolveSourceAsync(UserUrl, CancellationToken.None));
    }

    [Theory]
    [InlineData(SteamReviewSourceKind.User, "0")]
    [InlineData(SteamReviewSourceKind.User, "-1")]
    [InlineData(SteamReviewSourceKind.User, "invalid")]
    [InlineData(SteamReviewSourceKind.Curator, "-1")]
    [InlineData(SteamReviewSourceKind.Curator, "invalid")]
    [InlineData(SteamReviewSourceKind.Curator, "2147483648")]
    public async Task InvalidCursorRemainsAnArgumentErrorWithoutMakingRequest(SteamReviewSourceKind kind, string cursor){
        using var test = new TestClient();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => test.Client.GetReviewPageAsync(CreateSource(kind), cursor, CancellationToken.None));

        Assert.Equal("cursor", exception.ParamName);
        Assert.Empty(test.Requests);
    }

    [Theory]
    [InlineData(SteamReviewSourceKind.User, 0UL)]
    [InlineData(SteamReviewSourceKind.Curator, 0UL)]
    [InlineData(SteamReviewSourceKind.Curator, 4_294_967_296UL)]
    [InlineData((SteamReviewSourceKind)999, UserId)]
    public async Task InvalidStoredSourceRemainsAnArgumentErrorWithoutMakingRequest(SteamReviewSourceKind kind, ulong sourceId){
        using var test = new TestClient();

        var source = CreateSource() with{
            Kind = kind,
            Id = sourceId,
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => test.Client.GetReviewPageAsync(source, null, CancellationToken.None));

        Assert.Equal("source", exception.ParamName);
        Assert.Empty(test.Requests);
    }

    [Fact]
    public async Task UserPagesParseReviewsAndFollowPagination(){
        using var test = new TestClient();

        var source = CreateSource();

        test.Queue(UserPageUrl(1), CreateUserPage(3, CreateUserCard(570) + CreateUserCard(730, "Not Recommended"), "Showing 1-2 of 3 entries"));

        test.Queue(UserPageUrl(2), CreateUserPage(3, CreateUserCard(440), "Showing 3-3 of 3 entries", 2));

        var first = await test.Client.GetReviewPageAsync(source, null, CancellationToken.None);

        Assert.Equal("2", first.NextCursor);

        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        Assert.Collection(first.Reviews, review => {
            Assert.Equal(source, review.Source);
            Assert.Equal(570U, review.AppId);
            Assert.Equal(SteamReviewRecommendation.Recommended, review.Recommendation);
            Assert.Equal("Great & useful. Worth trying.", review.Text);
            Assert.Equal(UserUrl + "recommended/570/", review.Url);
            Assert.Equal("https://example.com/capsule.jpg", review.ThumbnailUrl);
            Assert.Null(review.AppName);
            Assert.Null(review.PublishedUtc);
            Assert.Null(review.ExternalReviewUrl);
        }, static review => {
            Assert.Equal(730U, review.AppId);
            Assert.Equal(SteamReviewRecommendation.NotRecommended, review.Recommendation);
        });

        var last = await test.Client.GetReviewPageAsync(source, first.NextCursor, CancellationToken.None);

        Assert.Null(last.NextCursor);
        Assert.Equal(440U, Assert.Single(last.Reviews).AppId);
        Assert.Equal(2, test.Requests.Count);
    }

    [Theory]
    [InlineData(SteamReviewSourceKind.User)]
    [InlineData(SteamReviewSourceKind.Curator)]
    public async Task ExplicitZeroCountProducesEmptyFinalPage(SteamReviewSourceKind kind){
        using var test = new TestClient();

        var source = CreateSource(kind);

        var body = kind == SteamReviewSourceKind.User ? CreateUserPage(0, string.Empty) : CreateCuratorResponse(string.Empty, 0);

        test.Queue(FirstPageUrl(kind), body);

        var page = await test.Client.GetReviewPageAsync(source, null, CancellationToken.None);

        Assert.Empty(page.Reviews);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task UserPageRejectsPositiveCountWithNoReviews(){
        using var test = new TestClient();

        test.Queue(UserPageUrl(1), CreateUserPage(1, string.Empty));

        await Assert.ThrowsAsync<InvalidDataException>(() => test.Client.GetReviewPageAsync(CreateSource(), null, CancellationToken.None));
    }

    [Fact]
    public async Task UserPageRejectsMissingPaginationWhenMoreReviewsExist(){
        using var test = new TestClient();

        test.Queue(UserPageUrl(1), CreateUserPage(2, CreateUserCard(570)));

        await Assert.ThrowsAsync<InvalidDataException>(() => test.Client.GetReviewPageAsync(CreateSource(), null, CancellationToken.None));
    }

    [Fact]
    public async Task UserPageRejectsPaginationCountMismatch(){
        using var test = new TestClient();

        test.Queue(UserPageUrl(1), CreateUserPage(2, CreateUserCard(570), "Showing 1-2 of 2 entries"));

        await Assert.ThrowsAsync<InvalidDataException>(() => test.Client.GetReviewPageAsync(CreateSource(), null, CancellationToken.None));
    }

    [Fact]
    public async Task UserPageRejectsUnrecognizedMarkupInsteadOfTreatingItAsEmpty(){
        using var test = new TestClient();

        test.Queue(UserPageUrl(1), "<html><body><h1>Please sign in</h1></body></html>");

        await Assert.ThrowsAsync<InvalidDataException>(() => test.Client.GetReviewPageAsync(CreateSource(), null, CancellationToken.None));
    }

    [Theory]
    [InlineData("Recommended", SteamReviewRecommendation.Recommended)]
    [InlineData("Not Recommended", SteamReviewRecommendation.NotRecommended)]
    [InlineData("Informational", SteamReviewRecommendation.Informational)]
    public async Task CuratorPageParsesReviewDetails(string label, SteamReviewRecommendation expectedRecommendation){
        using var test = new TestClient();

        var source = CreateSource(SteamReviewSourceKind.Curator);

        test.Queue(CuratorPageUrl(0), CreateCuratorResponse(CreateCuratorCard(570, label), 1));

        var page = await test.Client.GetReviewPageAsync(source, null, CancellationToken.None);

        var review = Assert.Single(page.Reviews);

        Assert.Null(page.NextCursor);
        Assert.Equal(source, review.Source);
        Assert.Equal(570U, review.AppId);
        Assert.Equal("Test Game", review.AppName);
        Assert.Equal(expectedRecommendation, review.Recommendation);
        Assert.Equal("Great & useful. Worth trying.", review.Text);
        Assert.Equal(CuratorUrl, review.Url);
        Assert.Equal("https://example.com/full-review", review.ExternalReviewUrl);
        Assert.Equal("https://example.com/capsule.jpg", review.ThumbnailUrl);
        Assert.Null(review.PublishedUtc);
    }

    [Fact]
    public async Task CuratorPagesFollowReturnedOffset(){
        using var test = new TestClient();

        var source = CreateSource(SteamReviewSourceKind.Curator);

        var firstPageCards = string.Concat(Enumerable.Range(1, 100).Select(static appId => CreateCuratorCard((uint)appId)));

        test.Queue(CuratorPageUrl(0), CreateCuratorResponse(firstPageCards, 101));

        test.Queue(CuratorPageUrl(100), CreateCuratorResponse(CreateCuratorCard(101), 101, 100));

        var first = await test.Client.GetReviewPageAsync(source, null, CancellationToken.None);

        Assert.Equal(100, first.Reviews.Count);
        Assert.Equal("100", first.NextCursor);

        var last = await test.Client.GetReviewPageAsync(source, first.NextCursor, CancellationToken.None);

        Assert.Null(last.NextCursor);
        Assert.Equal(101U, Assert.Single(last.Reviews).AppId);
        Assert.Equal(2, test.Requests.Count);
    }

    [Theory]
    [InlineData("""{"success":1,"start":"0","pagesize":"100","results_html":""}""")]
    [InlineData("""{"success":0,"total_count":0,"start":"0","pagesize":"100","results_html":""}""")]
    [InlineData("""{"success":1,"total_count":1,"start":"0","pagesize":"100","results_html":""}""")]
    [InlineData("""{"success":1,"total_count":0,"start":"0","pagesize":"0","results_html":""}""")]
    [InlineData("""{"success":1,"total_count":1,"start":"1","pagesize":"100","results_html":""}""")]
    public async Task CuratorPageRejectsInvalidOrInconsistentEnvelope(string response){
        using var test = new TestClient();

        test.Queue(CuratorPageUrl(0), response);

        await Assert.ThrowsAsync<InvalidDataException>(() => test.Client.GetReviewPageAsync(CreateSource(SteamReviewSourceKind.Curator), null, CancellationToken.None));
    }

    [Theory]
    [InlineData(SteamReviewSourceKind.User)]
    [InlineData(SteamReviewSourceKind.Curator)]
    public async Task PageRejectsDuplicateAppIds(SteamReviewSourceKind kind){
        using var test = new TestClient();

        var body = kind == SteamReviewSourceKind.User ? CreateUserPage(2, CreateUserCard(570) + CreateUserCard(570)) : CreateCuratorResponse(CreateCuratorCard(570) + CreateCuratorCard(570), 2);

        test.Queue(FirstPageUrl(kind), body);

        await Assert.ThrowsAsync<InvalidDataException>(() => test.Client.GetReviewPageAsync(CreateSource(kind), null, CancellationToken.None));
    }

    [Theory]
    [InlineData(SteamReviewSourceKind.User)]
    [InlineData(SteamReviewSourceKind.Curator)]
    public async Task PageRejectsUnknownRecommendationLabel(SteamReviewSourceKind kind){
        using var test = new TestClient();

        var body = kind == SteamReviewSourceKind.User ? CreateUserPage(1, CreateUserCard(570, "Unexpected label")) : CreateCuratorResponse(CreateCuratorCard(570, "Unexpected label"), 1);

        test.Queue(FirstPageUrl(kind), body);

        await Assert.ThrowsAsync<InvalidDataException>(() => test.Client.GetReviewPageAsync(CreateSource(kind), null, CancellationToken.None));
    }

    [Theory]
    [InlineData(SteamReviewSourceKind.User)]
    [InlineData(SteamReviewSourceKind.Curator)]
    public async Task EmptyHttpBodyIsNotReportedAsAnEmptyPage(SteamReviewSourceKind kind){
        using var test = new TestClient();

        test.Queue(FirstPageUrl(kind), string.Empty);

        await Assert.ThrowsAsync<InvalidDataException>(() => test.Client.GetReviewPageAsync(CreateSource(kind), null, CancellationToken.None));
    }

    [Theory]
    [InlineData(SteamReviewSourceKind.User)]
    [InlineData(SteamReviewSourceKind.Curator)]
    public async Task HttpFailureIsNotReportedAsAnEmptyPage(SteamReviewSourceKind kind){
        using var test = new TestClient();

        test.Queue(FirstPageUrl(kind), "Too many requests", HttpStatusCode.TooManyRequests);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => test.Client.GetReviewPageAsync(CreateSource(kind), null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
    }

    private static SteamReviewSource CreateSource(SteamReviewSourceKind kind = SteamReviewSourceKind.User) => new(){
        Kind = kind,
        Id = kind == SteamReviewSourceKind.User ? UserId : 1_850UL,
        Name = kind == SteamReviewSourceKind.User ? "Test Reviewer" : "PC Gamer",
        Url = kind == SteamReviewSourceKind.User ? UserUrl : CuratorUrl,
    };

    private static string CreateProfileHtml(ulong steamId = UserId){
        var data = JsonSerializer.Serialize(new{
            steamid = steamId.ToString(CultureInfo.InvariantCulture),
            personaname = "Test Reviewer",
            url = UserUrl,
            summary = "Text containing }; must not truncate the JSON object.",
        });

        return $$"""
                 <html>
                   <body>
                     <script>
                       var unrelated = {};
                       g_rgProfileData = {{data}};
                       var afterProfileData = true;
                     </script>
                   </body>
                 </html>
                 """;
    }

    private static string CreateCuratorProfileHtml(uint curatorId = 1_850){
        var id = curatorId.ToString(CultureInfo.InvariantCulture);

        return $"""
                 <html>
                   <body>
                     <h2 class="pageheader curator_name">
                       <a href="https://store.steampowered.com/curator/{id}-PC-Gamer/">PC Gamer</a>
                     </h2>
                   </body>
                 </html>
                 """;
    }

    private static string CreateUserPage(
        int total,
        string cards,
        string? paging = null,
        int currentPage = 1){
        var count = total.ToString(CultureInfo.InvariantCulture);
        var page = currentPage.ToString(CultureInfo.InvariantCulture);

        var pagination = paging is null
            ? string.Empty
            : $"""
                <div class="workshopBrowsePaging">
                  <div class="workshopBrowsePagingInfo">{paging}</div>
                  <div class="workshopBrowsePagingControls">
                    <span class="page_current">{page}</span>
                  </div>
                </div>
                """;

        return $"""
                 <html>
                   <body>
                     <div id="tabs_basebg" class="review_list">
                       <div id="rightContents">
                         <div class="review_stat">
                           <div class="giantNumber">{count}</div>
                           <div class="giantNumberSubhead">Products<br>reviewed</div>
                         </div>
                         <div class="review_stat">
                           <div class="giantNumber">999</div>
                           <div class="giantNumberSubhead">Products<br>in account</div>
                         </div>
                       </div>
                       <div id="leftContents">
                         <h1>Recent reviews by Test Reviewer</h1>
                         {pagination}
                         {cards}
                       </div>
                     </div>
                   </body>
                 </html>
                 """;
    }

    private static string CreateUserCard(uint appId, string label = "Recommended"){
        var id = appId.ToString(CultureInfo.InvariantCulture);

        return $"""
                 <div class="review_box">
                   <div class="header">Helpful vote information is not review text.</div>
                   <div class="review_box_content">
                     <div class="leftcol">
                       <a class="game_capsule_ctn" href="https://steamcommunity.com/app/{id}">
                         <img class="game_capsule" src="https://example.com/capsule.jpg">
                       </a>
                     </div>
                     <div class="rightcol">
                       <div class="vote_header">
                         <div class="title">
                           <a href="{UserUrl}recommended/{id}/">{label}</a>
                         </div>
                         <div class="hours">12 hours on record</div>
                       </div>
                       <div class="content">Great &amp; useful.<br>Worth trying.</div>
                       <div class="posted">Posted 7 September.</div>
                     </div>
                   </div>
                 </div>
                 """;
    }

    private static string CreateCuratorCard(uint appId, string label = "Recommended"){
        var id = appId.ToString(CultureInfo.InvariantCulture);

        return $"""
                 <div class="recommendation">
                   <div>
                     <a class="store_capsule" data-ds-appid="{id}"
                        href="https://store.steampowered.com/app/{id}/">
                       <div class="capsule">
                         <img src="https://example.com/capsule.jpg" alt="Test Game">
                       </div>
                     </a>
                   </div>
                   <a class="recommendation_link"
                      href="https://store.steampowered.com/app/{id}/?curator_clanid=1850">
                     <div class="recommendation_midcol">
                       <div class="recommendation_type_ctn">
                         <span>{label}</span>
                         <span class="curator_review_date">2 September</span>
                       </div>
                       <div class="recommendation_desc">Great &amp; useful.<br>Worth trying.</div>
                       <div class="recommendation_readmore">
                         <a href="https://example.com/full-review">Read the full review</a>
                       </div>
                     </div>
                   </a>
                 </div>
                 """;
    }

    private static string CreateCuratorResponse(string html, int total, int start = 0) =>
        JsonSerializer.Serialize(new{
            success = 1,
            pagesize = "100",
            total_count = total,
            start = start.ToString(CultureInfo.InvariantCulture),
            results_html = html,
        });

    private static string FirstPageUrl(SteamReviewSourceKind kind) => kind == SteamReviewSourceKind.User ? UserPageUrl(1) : CuratorPageUrl(0);

    private static string UserPageUrl(int page) => $"{UserUrl}recommended/?l=english&p={page.ToString(CultureInfo.InvariantCulture)}";

    private static string CuratorPageUrl(int start) => $"{CuratorUrl}ajaxgetfilteredrecommendations/?start={start.ToString(CultureInfo.InvariantCulture)}&count=100&sort=recent&l=english";

    private sealed class TestClient : IHttpClientFactory, IDisposable{
        private readonly StubHandler _handler = new();

        public SteamReviewClient Client{ get; }

        public List<string> Requests => _handler.Requests;

        public TestClient() => Client = new SteamReviewClient(this);

        public void Queue(string url, string body, HttpStatusCode statusCode = HttpStatusCode.OK){
            _handler.Responses.Enqueue(new FixtureResponse(url, body, statusCode));
        }

        public HttpClient CreateClient(string name){
            Assert.Equal(SteamReviewClient.HttpClientName, name);

            return new HttpClient(_handler, false);
        }

        public void Dispose(){
            _handler.Dispose();
        }
    }

    private sealed record FixtureResponse(string Url, string Body, HttpStatusCode StatusCode);

    private sealed class StubHandler : HttpMessageHandler{
        public Queue<FixtureResponse> Responses{ get; } = [];
        public List<string> Requests{ get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken){
            cancellationToken.ThrowIfCancellationRequested();

            var uri = request.RequestUri;
            Assert.NotNull(uri);

            Requests.Add(uri.AbsoluteUri);

            Assert.True(Responses.Count > 0, $"Unexpected HTTP request: {uri}");

            var expected = Responses.Dequeue();

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(expected.Url, uri.AbsoluteUri);

            return Task.FromResult(new HttpResponseMessage(expected.StatusCode){
                Content = new StringContent(expected.Body),
                RequestMessage = request,
            });
        }
    }
}