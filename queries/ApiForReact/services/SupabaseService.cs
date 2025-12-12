using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using queries.models;
using SupabaseClient = Supabase.Client;
using SupabaseOptions = Supabase.SupabaseOptions;

namespace queries.services;

public class SupabaseService
{
    private readonly string _url;
    private readonly string _key;
    private readonly SupabaseOptions _options;
    public SupabaseClient? _supabase { get; private set; }
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public SupabaseService()
    {
        var tokenReader = new TokenReader.TokenReader();
        _url = tokenReader.GetSupabaseUrl();
        _key = tokenReader.GetSupabaseKey();
        _options = new SupabaseOptions
        {
            AutoConnectRealtime = true
        };

        // Do not throw during construction — defer validation until an operation requires Supabase.
        if (!string.IsNullOrWhiteSpace(_url) && !string.IsNullOrWhiteSpace(_key))
        {
            try
            {
                _supabase = new SupabaseClient(_url, _key, _options);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to create Supabase client during startup: {ex.Message}");
                _supabase = null;
            }
        }

        _httpClient = new HttpClient();
        if (!string.IsNullOrWhiteSpace(_url))
        {
            try
            {
                _httpClient.BaseAddress = new Uri(_url);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Invalid SUPABASE_URL value: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(_key))
        {
            _httpClient.DefaultRequestHeaders.Add("apikey", _key);
        }
        _jsonOptions.Converters.Add(new FlexibleStringConverter());
    }

    public async Task InitializeAsync()
    {
        await EnsureInitializedAsync();
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_url) || string.IsNullOrWhiteSpace(_key))
            {
                throw new InvalidOperationException("Supabase URL or Key is not configured. Set the SUPABASE_URL/SUPABASE_KEY environment variables or provide them in services/tokens.json.");
            }

            if (_supabase == null)
            {
                _supabase = new SupabaseClient(_url, _key, _options);
            }

            await _supabase.InitializeAsync();
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private HttpRequestMessage CreateAuthRequest(HttpMethod method, string path, object? body = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, path);
        var token = bearer ?? _key;
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("Bearer token or service key is required.");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: _jsonOptions);
        }

        return request;
    }

    private Task<HttpResponseMessage> SendAuthRequestAsync(HttpMethod method, string path, object? body = null, string? bearer = null)
    {
        // If BaseAddress is not configured and path is relative, fail with a clear message
        if ((_httpClient.BaseAddress == null || string.IsNullOrWhiteSpace(_url)) && !Uri.IsWellFormedUriString(path, UriKind.Absolute))
        {
            throw new InvalidOperationException("Supabase URL is not configured. Set the SUPABASE_URL environment variable or provide it in services/tokens.json (BotSettings.SupabaseUrl).");
        }

        return _httpClient.SendAsync(CreateAuthRequest(method, path, body, bearer));
    }

    private Task<HttpResponseMessage> SendAuthRequestAsyncRequired(HttpMethod method, string path, object? body, string accessToken)
    {
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new InvalidOperationException("Access token is required for this operation.");
        }
        return SendAuthRequestAsync(method, path, body, accessToken);
    }

    private async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions);
    }

    private static string FormatInList(IEnumerable<Guid> ids)
        => string.Join(',', ids.Select(id => id.ToString()));

    public async Task<SupabaseUser?> GetUserAsync(string accessToken)
    {
        var response = await SendAuthRequestAsync(HttpMethod.Get, "/auth/v1/user", null, accessToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            var decoded = TryDecodeUserFromToken(accessToken);
            if (decoded != null)
            {
                return decoded;
            }

            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[WARN] Failed to resolve user from Supabase: {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
            return null;
        }

        return await response.Content.ReadFromJsonAsync<SupabaseUser>(_jsonOptions);
    }

    private SupabaseUser? TryDecodeUserFromToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);

            var subject = jwt.Claims.FirstOrDefault(claim => claim.Type == "sub")?.Value;
            if (!Guid.TryParse(subject, out var userId))
            {
                return null;
            }

            var email = jwt.Claims.FirstOrDefault(claim => claim.Type == "email")?.Value;
            var role = jwt.Claims.FirstOrDefault(claim => claim.Type == "role")?.Value;

            return new SupabaseUser
            {
                Id = userId,
                Email = string.IsNullOrWhiteSpace(email) ? null : email,
                Role = string.IsNullOrWhiteSpace(role) ? null : role
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to decode Supabase access token: {ex.Message}");
            return null;
        }
    }

    public async Task<List<ProfileDto>> GetProfilesByIdsAsync(IEnumerable<Guid> ids, string accessToken)
    {
        var idList = ids.ToList();
        if (!idList.Any())
        {
            return new List<ProfileDto>();
        }

        return await FetchProfilesAsync($"id=in.({FormatInList(idList)})", accessToken);
    }

    public async Task<ProfileDto?> GetProfileByIdAsync(Guid userId, string accessToken)
    {
        var data = await FetchProfilesAsync($"id=eq.{userId}", accessToken);
        return data.FirstOrDefault();
    }

    public async Task<ProfileDto?> GetProfileByUsernameAsync(string username, string accessToken)
    {
        var path = $"/rest/v1/profiles?select=id,username,full_name,avatar_url,bio,banner_url&username=ilike.{username}";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var data = await ReadJsonAsync<List<ProfileDto>>(response) ?? new List<ProfileDto>();
        return data.FirstOrDefault();
    }

    public async Task<List<TrackRecord>> GetTracksAsync(Guid userId, string accessToken)
    {
        var path = $"/rest/v1/tracks?select=id,title,plays_count,likes_count,user_id,audio_url,cover_url,duration_seconds&user_id=eq.{userId}&order=created_at.desc&limit=6";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<TrackRecord>();
        }

        return await ReadJsonAsync<List<TrackRecord>>(response) ?? new List<TrackRecord>();
    }

    public async Task<List<TrackLikeRecord>> GetLikedTracksAsync(Guid userId, string accessToken)
    {
        var path = $"/rest/v1/track_likes?select=track:tracks(id,title,plays_count,likes_count,user_id,audio_url,cover_url,duration_seconds)&user_id=eq.{userId}&order=created_at.desc&limit=6";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<TrackLikeRecord>();
        }

        return await ReadJsonAsync<List<TrackLikeRecord>>(response) ?? new List<TrackLikeRecord>();
    }

    public async Task<List<Guid>> GetRecentTrackOwnerIdsAsync(int limit, string accessToken)
    {
        if (limit <= 0)
        {
            return new List<Guid>();
        }

        var path = $"/rest/v1/tracks?select=user_id&order=created_at.desc&limit={limit}";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<Guid>();
        }

        var owners = await ReadJsonAsync<List<UserIdRecord>>(response) ?? new List<UserIdRecord>();
        return owners
            .Select(o => o.UserId)
            .Where(id => id != Guid.Empty)
            .ToList();
    }

    public async Task<List<ListeningHistoryRecord>> GetListeningHistoryAsync(Guid userId, string accessToken)
    {
        var path = $"/rest/v1/listening_history?select=played_at,track:tracks(id,title,plays_count,likes_count,user_id,audio_url,cover_url,duration_seconds)&user_id=eq.{userId}&order=played_at.desc&limit=6";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<ListeningHistoryRecord>();
        }

        return await ReadJsonAsync<List<ListeningHistoryRecord>>(response) ?? new List<ListeningHistoryRecord>();
    }

    public async Task RecordListeningHistoryAsync(Guid userId, long trackId, string accessToken, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || trackId <= 0)
        {
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["user_id"] = userId,
            ["track_id"] = trackId,
            ["played_at"] = DateTimeOffset.UtcNow
        };

        var request = CreateAuthRequest(HttpMethod.Post, "/rest/v1/listening_history?on_conflict=user_id,track_id", payload, accessToken);
        request.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            await UpsertListeningHistoryWithServiceKeyAsync(payload, cancellationToken);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to record listening history." : body, null, response.StatusCode);
    }

    private async Task UpsertListeningHistoryWithServiceKeyAsync(Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var fallbackRequest = CreateAuthRequest(HttpMethod.Post, "/rest/v1/listening_history?on_conflict=user_id,track_id", payload);
        fallbackRequest.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");

        using var fallbackResponse = await _httpClient.SendAsync(fallbackRequest, cancellationToken);
        if (fallbackResponse.IsSuccessStatusCode || fallbackResponse.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        var body = await fallbackResponse.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to record listening history." : body, null, fallbackResponse.StatusCode);
    }

    public async Task<List<TrackRecord>> GetLatestTracksForUsersAsync(IEnumerable<Guid> userIds, int limit, string accessToken, CancellationToken cancellationToken = default)
    {
        var idList = userIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (!idList.Any())
        {
            return new List<TrackRecord>();
        }

        var effectiveLimit = limit > 0 ? limit : 20;
        var path = $"/rest/v1/tracks?select=id,title,plays_count,likes_count,user_id,audio_url,cover_url,duration_seconds,created_at&user_id=in.({FormatInList(idList)})&is_private=is.false&order=created_at.desc&limit={effectiveLimit}";

        using var response = await _httpClient.SendAsync(CreateAuthRequest(HttpMethod.Get, path, null, accessToken), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<TrackRecord>();
        }

        return await response.Content.ReadFromJsonAsync<List<TrackRecord>>(_jsonOptions, cancellationToken) ?? new List<TrackRecord>();
    }

    public async Task<List<FollowingRelation>> GetFollowingRelationsAsync(Guid userId, string accessToken)
    {
        var path = $"/rest/v1/follows?select=following_id&follower_id=eq.{userId}&order=created_at.desc&limit=6";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<FollowingRelation>();
        }

        return await ReadJsonAsync<List<FollowingRelation>>(response) ?? new List<FollowingRelation>();
    }

    public async Task<List<FollowingRelation>> GetFollowingRelationsAsync(Guid userId, IEnumerable<Guid> candidateIds, string accessToken)
    {
        var idList = candidateIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (!idList.Any())
        {
            return new List<FollowingRelation>();
        }

        var path = $"/rest/v1/follows?select=following_id&follower_id=eq.{userId}&following_id=in.({FormatInList(idList)})";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<FollowingRelation>();
        }

        return await ReadJsonAsync<List<FollowingRelation>>(response) ?? new List<FollowingRelation>();
    }

    public async Task<List<Guid>> GetFollowingIdsAsync(Guid userId, string accessToken, int limit = 64)
    {
        var limitClause = limit > 0 ? $"&limit={limit}" : string.Empty;
        var path = $"/rest/v1/follows?select=following_id&follower_id=eq.{userId}&order=created_at.desc{limitClause}";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<Guid>();
        }

        var payload = await ReadJsonAsync<List<FollowingRelation>>(response) ?? new List<FollowingRelation>();
        return payload
            .Select(relation => relation.FollowingId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
    }

    public async Task<List<FollowerCount>> GetFollowerCountsAsync(IEnumerable<Guid> ids, string accessToken)
    {
        var idList = ids.ToList();
        if (!idList.Any())
        {
            return new List<FollowerCount>();
        }

        var path = $"/rest/v1/follows?select=following_id&following_id=in.({FormatInList(idList)})";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<FollowerCount>();
        }

        return await ReadJsonAsync<List<FollowerCount>>(response) ?? new List<FollowerCount>();
    }

    public async Task<List<TrackCountRecord>> GetTracksCountsForUsersAsync(IEnumerable<Guid> ids, string accessToken)
    {
        var idList = ids.ToList();
        if (!idList.Any())
        {
            return new List<TrackCountRecord>();
        }

        var path = $"/rest/v1/tracks?select=user_id&user_id=in.({FormatInList(idList)})";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<TrackCountRecord>();
        }

        return await ReadJsonAsync<List<TrackCountRecord>>(response) ?? new List<TrackCountRecord>();
    }

    public async Task<int> GetTracksCountAsync(Guid userId, string accessToken)
        => await GetCountAsync($"/rest/v1/tracks?select=id&user_id=eq.{userId}", accessToken);

    public async Task<int> GetLikesCountAsync(Guid userId, string accessToken)
        => await GetCountAsync($"/rest/v1/track_likes?select=track_id&user_id=eq.{userId}", accessToken);

    public async Task<int> GetFollowersCountAsync(Guid userId, string accessToken)
        => await GetCountAsync($"/rest/v1/follows?select=follower_id&following_id=eq.{userId}", accessToken);

    public async Task<int> GetFollowingCountAsync(Guid userId, string accessToken)
        => await GetCountAsync($"/rest/v1/follows?select=following_id&follower_id=eq.{userId}", accessToken);

    private async Task<int> GetCountAsync(string path, string accessToken)
    {
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        var payload = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(payload)) return 0;

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.GetArrayLength()
            : 0;
    }

    private async Task<int> GetCountWithServiceKeyAsync(string path, CancellationToken cancellationToken)
    {
        using var request = CreateAuthRequest(HttpMethod.Get, path, null, _key);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return 0;
        }

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.GetArrayLength()
            : 0;
    }

    public Task<HttpResponseMessage> SignUpAsync(string email, string password)
        => SendAuthRequestAsync(HttpMethod.Post, "/auth/v1/signup", new { email, password });

    public Task<HttpResponseMessage> SignInAsync(string email, string password)
        => SendAuthRequestAsync(HttpMethod.Post, "/auth/v1/token?grant_type=password", new { email, password });

    public Task<HttpResponseMessage> SignOutAsync(string accessToken)
        => SendAuthRequestAsync(HttpMethod.Post, "/auth/v1/logout", null, accessToken);

    public async Task<HttpResponseMessage> UpsertProfileAsync(ProfileUpsertRequest request, string accessToken)
    {
        var user = await GetUserAsync(accessToken);
        if (user == null)
        {
            throw new InvalidOperationException("Unable to identify the authenticated user.");
        }

        var payload = BuildProfilePayload(user.Id, request);
        var response = await SendUpsertAsync(payload, accessToken);
        return response;
    }

    // Upsert profile using the service key when we already know the target user id
    public async Task<HttpResponseMessage> UpsertProfileWithServiceKeyAsync(Guid userId, ProfileUpsertRequest request)
    {
        var payload = BuildProfilePayload(userId, request);
        var response = await SendUpsertAsync(payload, _key);
        return response;
    }

    private static Dictionary<string, object?> BuildProfilePayload(Guid userId, ProfileUpsertRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = userId,
            ["username"] = request.Username
        };

        if (!string.IsNullOrWhiteSpace(request.FullName))
        {
            payload["full_name"] = request.FullName;
        }

        if (!string.IsNullOrWhiteSpace(request.AvatarUrl))
        {
            payload["avatar_url"] = request.AvatarUrl;
        }

        if (!string.IsNullOrWhiteSpace(request.BannerUrl))
        {
            payload["banner_url"] = request.BannerUrl;
        }

        if (!string.IsNullOrWhiteSpace(request.Bio))
        {
            payload["bio"] = request.Bio;
        }

        return payload;
    }

    private async Task<HttpResponseMessage> SendUpsertAsync(Dictionary<string, object?> payload, string accessToken)
    {
        var path = "/rest/v1/profiles?on_conflict=id";
        // Use Prefer: resolution=merge-duplicates so Supabase performs an upsert instead of throwing on duplicate keys
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Prefer", "resolution=merge-duplicates");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[ERROR] Supabase upsert request failed");
            Console.WriteLine($"  Status: {response.StatusCode}");
            Console.WriteLine($"  Path: {path}");
            Console.WriteLine($"  Payload: {JsonSerializer.Serialize(payload)}");
            Console.WriteLine($"  Error: {errorContent}");
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(errorContent)
                    ? $"Supabase upsert failed with status {(int)response.StatusCode}."
                    : errorContent,
                null,
                response.StatusCode);
        }

        return response;
    }

    private async Task<List<ProfileDto>> FetchProfilesAsync(string filter, string accessToken)
    {
        var path = $"/rest/v1/profiles?select=id,username,full_name,avatar_url,bio,banner_url&{filter}";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<ProfileDto>();
        }
        return await ReadJsonAsync<List<ProfileDto>>(response) ?? new List<ProfileDto>();
    }

    public async Task<List<ProfileDto>> SearchProfilesAsync(string query, string accessToken)
    {
        var path = $"/rest/v1/profiles?select=id,username,full_name,avatar_url&or=(username.ilike.*{query}*,full_name.ilike.*{query}*)&limit=10";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<ProfileDto>();
        }

        return await ReadJsonAsync<List<ProfileDto>>(response) ?? new List<ProfileDto>();
    }

    public async Task<List<TrackSearchResult>> SearchTracksAsync(string query, string accessToken)
    {
        var path = $"/rest/v1/tracks?select=id,title,user_id&title=ilike.*{query}*&limit=10";
        var response = await SendAuthRequestAsync(HttpMethod.Get, path, null, accessToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<TrackSearchResult>();
        }

        return await ReadJsonAsync<List<TrackSearchResult>>(response) ?? new List<TrackSearchResult>();
    }

    public async Task<TrackDetailRecord?> GetTrackDetailAsync(long trackId, Guid requesterId, string accessToken, CancellationToken cancellationToken = default)
    {
        var track = await GetTrackDetailInternalAsync(trackId, accessToken, "user", cancellationToken);
        if (track != null)
        {
            return await EnrichTrackDetailAsync(track, cancellationToken);
        }

        var fallback = await GetTrackDetailInternalAsync(trackId, _key, "service", cancellationToken);
        if (fallback == null)
        {
            return null;
        }

        if (fallback.IsPrivate == true && fallback.UserId != requesterId)
        {
            Console.WriteLine($"[INFO] Track {trackId} resolved via service fallback but is private; requester {requesterId} is not the owner.");
            return null;
        }

        Console.WriteLine($"[INFO] Track {trackId} resolved via service fallback for requester {requesterId}.");
        return await EnrichTrackDetailAsync(fallback, cancellationToken);
    }

    private async Task<TrackDetailRecord?> GetTrackDetailInternalAsync(long trackId, string bearer, string mode, CancellationToken cancellationToken)
    {
        var path = $"/rest/v1/tracks?select=id,title,description,audio_url,cover_url,waveform_data,duration_seconds,plays_count,likes_count,is_private,created_at,user_id&id=eq.{trackId}&limit=1";
        using var response = await _httpClient.SendAsync(CreateAuthRequest(HttpMethod.Get, path, null, bearer), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"[WARN] Track {trackId} request via {mode} token failed with status {(int)response.StatusCode}: {response.StatusCode}. Body: {details}");
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<List<TrackDetailRecord>>(_jsonOptions, cancellationToken)
                      ?? new List<TrackDetailRecord>();

        if (payload.Count == 0)
        {
            Console.WriteLine($"[DEBUG] Track {trackId} not returned in {mode} query (empty result).");
            return null;
        }

        return payload[0];
    }

    private async Task<TrackDetailRecord> EnrichTrackDetailAsync(TrackDetailRecord track, CancellationToken cancellationToken)
    {
        try
        {
            var likesCount = await GetCountWithServiceKeyAsync($"/rest/v1/track_likes?select=track_id&track_id=eq.{track.Id}", cancellationToken);
            return track with { LikesCount = likesCount };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to enrich track {track.Id} with like count: {ex.Message}");
            return track;
        }
    }

    public async Task<bool> IsTrackLikedAsync(Guid userId, long trackId, string accessToken)
        => (await GetCountAsync($"/rest/v1/track_likes?select=track_id&user_id=eq.{userId}&track_id=eq.{trackId}", accessToken)) > 0;

    public async Task<bool> IsFollowingAsync(Guid followerId, Guid followingId, string accessToken)
        => (await GetCountAsync($"/rest/v1/follows?select=following_id&follower_id=eq.{followerId}&following_id=eq.{followingId}", accessToken)) > 0;

    public async Task LikeTrackAsync(Guid userId, long trackId, string accessToken, CancellationToken cancellationToken = default)
    {
        if (await IsTrackLikedAsync(userId, trackId, accessToken))
        {
            return;
        }

        var payload = new[]
        {
            new Dictionary<string, object>
            {
                ["user_id"] = userId,
                ["track_id"] = trackId
            }
        };

        var request = CreateAuthRequest(HttpMethod.Post, "/rest/v1/track_likes", payload, accessToken);
        request.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await UpdateTrackLikesCountAsync(trackId, 1, accessToken, cancellationToken);
        }
        else if (response.StatusCode != HttpStatusCode.Conflict)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to like track." : body, null, response.StatusCode);
        }
    }

    public async Task UnlikeTrackAsync(Guid userId, long trackId, string accessToken, CancellationToken cancellationToken = default)
    {
        if (!await IsTrackLikedAsync(userId, trackId, accessToken))
        {
            return;
        }

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/rest/v1/track_likes?track_id=eq.{trackId}&user_id=eq.{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("apikey", _key);
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await UpdateTrackLikesCountAsync(trackId, -1, accessToken, cancellationToken);
        }
        else if (response.StatusCode != HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to unlike track." : body, null, response.StatusCode);
        }
    }

    public async Task<bool> IsPlaylistLikedAsync(Guid userId, string playlistId, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return false;
        }

        var encodedPlaylistId = Uri.EscapeDataString(playlistId);
        return (await GetCountAsync($"/rest/v1/playlist_likes?select=playlist_id&user_id=eq.{userId}&playlist_id=eq.{encodedPlaylistId}", accessToken)) > 0;
    }

    public async Task LikePlaylistAsync(Guid userId, string playlistId, string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new HttpRequestException("Playlist id is required.", null, HttpStatusCode.BadRequest);
        }

        if (await IsPlaylistLikedAsync(userId, playlistId, accessToken))
        {
            return;
        }

        var payload = new[]
        {
            new Dictionary<string, object>
            {
                ["user_id"] = userId,
                ["playlist_id"] = playlistId
            }
        };

        var request = CreateAuthRequest(HttpMethod.Post, "/rest/v1/playlist_likes", payload, accessToken);
        request.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await UpdatePlaylistLikesCountAsync(playlistId, 1, cancellationToken);
        }
        else if (response.StatusCode != HttpStatusCode.Conflict)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to like playlist." : body, null, response.StatusCode);
        }
    }

    public async Task UnlikePlaylistAsync(Guid userId, string playlistId, string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new HttpRequestException("Playlist id is required.", null, HttpStatusCode.BadRequest);
        }

        if (!await IsPlaylistLikedAsync(userId, playlistId, accessToken))
        {
            return;
        }

        var encodedPlaylistId = Uri.EscapeDataString(playlistId);
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/rest/v1/playlist_likes?playlist_id=eq.{encodedPlaylistId}&user_id=eq.{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("apikey", _key);
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await UpdatePlaylistLikesCountAsync(playlistId, -1, cancellationToken);
        }
        else if (response.StatusCode != HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to unlike playlist." : body, null, response.StatusCode);
        }
    }

    public async Task FollowUserAsync(Guid followerId, Guid followingId, string accessToken, CancellationToken cancellationToken = default)
    {
        if (await IsFollowingAsync(followerId, followingId, accessToken))
        {
            return;
        }

        var payload = new[]
        {
            new Dictionary<string, object>
            {
                ["follower_id"] = followerId,
                ["following_id"] = followingId
            }
        };

        var request = CreateAuthRequest(HttpMethod.Post, "/rest/v1/follows", payload, accessToken);
        request.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await UpdateProfileSubscribeCountAsync(followingId, 1, accessToken, cancellationToken);
        }
        else if (response.StatusCode != HttpStatusCode.Conflict)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to follow user." : body, null, response.StatusCode);
        }
    }

    public async Task UnfollowUserAsync(Guid followerId, Guid followingId, string accessToken, CancellationToken cancellationToken = default)
    {
        if (!await IsFollowingAsync(followerId, followingId, accessToken))
        {
            return;
        }

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/rest/v1/follows?follower_id=eq.{followerId}&following_id=eq.{followingId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("apikey", _key);
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await UpdateProfileSubscribeCountAsync(followingId, -1, accessToken, cancellationToken);
        }
        else if (response.StatusCode != HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to unfollow user." : body, null, response.StatusCode);
        }
    }

    public async Task<List<PlaylistSummary>> GetLatestPlaylistsForUsersAsync(IEnumerable<Guid> userIds, int limit, string accessToken, CancellationToken cancellationToken = default)
    {
        var idList = userIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (!idList.Any())
        {
            return new List<PlaylistSummary>();
        }

        var effectiveLimit = limit > 0 ? limit : 20;
        var path = $"/rest/v1/playlists?select=id,user_id,title,description,cover_url,is_private,created_at,likes_count&user_id=in.({FormatInList(idList)})&is_private=is.false&order=created_at.desc&limit={effectiveLimit}";

        using var response = await _httpClient.SendAsync(CreateAuthRequest(HttpMethod.Get, path, null, accessToken), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<PlaylistSummary>();
        }

        var raw = await response.Content.ReadFromJsonAsync<List<PlaylistRecord>>(_jsonOptions, cancellationToken) ?? new List<PlaylistRecord>();
        if (!raw.Any())
        {
            return new List<PlaylistSummary>();
        }

        var counts = await GetPlaylistTrackCountsAsync(raw.Select(record => record.Id), cancellationToken);

        return raw.Select(record => new PlaylistSummary
        {
            Id = record.Id,
            UserId = record.UserId,
            Title = record.Title,
            Description = record.Description,
            CoverUrl = record.CoverUrl,
            IsPrivate = record.IsPrivate ?? false,
            TrackCount = counts.TryGetValue(record.Id, out var count) ? count : 0,
            CreatedAt = record.CreatedAt,
            LikesCount = record.LikesCount
        }).ToList();
    }

    public async Task<List<PlaylistSummary>> GetPlaylistsAsync(Guid ownerId, Guid viewerId, string accessToken, CancellationToken cancellationToken = default)
    {
        var filter = ownerId == viewerId ? string.Empty : "&is_private=is.false";
        var path = $"/rest/v1/playlists?select=id,user_id,title,description,cover_url,is_private,created_at,likes_count&user_id=eq.{ownerId}{filter}&order=created_at.desc";
        using var response = await _httpClient.SendAsync(CreateAuthRequest(HttpMethod.Get, path, null, accessToken), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<PlaylistSummary>();
        }

        var raw = await response.Content.ReadFromJsonAsync<List<PlaylistRecord>>(_jsonOptions, cancellationToken) ?? new List<PlaylistRecord>();
        if (!raw.Any())
        {
            return new List<PlaylistSummary>();
        }

        var counts = await GetPlaylistTrackCountsAsync(raw.Select(record => record.Id), cancellationToken);

        return raw.Select(record => new PlaylistSummary
        {
            Id = record.Id,
            UserId = record.UserId,
            Title = record.Title,
            Description = record.Description,
            CoverUrl = record.CoverUrl,
            IsPrivate = record.IsPrivate ?? false,
            TrackCount = counts.TryGetValue(record.Id, out var count) ? count : 0,
            CreatedAt = record.CreatedAt,
            LikesCount = record.LikesCount
        }).ToList();
    }

    public async Task<List<PlaylistSummary>> GetLikedPlaylistsAsync(Guid userId, string accessToken, CancellationToken cancellationToken = default)
    {
        var path = $"/rest/v1/playlist_likes?select=playlist:playlists(id,user_id,title,description,cover_url,is_private,created_at,likes_count)&user_id=eq.{userId}&order=created_at.desc";
        using var response = await _httpClient.SendAsync(CreateAuthRequest(HttpMethod.Get, path, null, accessToken), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<PlaylistSummary>();
        }

        var liked = await response.Content.ReadFromJsonAsync<List<PlaylistLikeRecord>>(_jsonOptions, cancellationToken) ?? new List<PlaylistLikeRecord>();
        if (!liked.Any())
        {
            return new List<PlaylistSummary>();
        }

        var playlistRecords = liked
            .Select(record => record.Playlist)
            .Where(record => record != null)
            .Where(record => record.IsPrivate != true || record.UserId == userId)
            .ToList();

        if (!playlistRecords.Any())
        {
            return new List<PlaylistSummary>();
        }

        var counts = await GetPlaylistTrackCountsAsync(playlistRecords.Select(record => record.Id), cancellationToken);

        return playlistRecords.Select(record => new PlaylistSummary
        {
            Id = record.Id,
            UserId = record.UserId,
            Title = record.Title,
            Description = record.Description,
            CoverUrl = record.CoverUrl,
            IsPrivate = record.IsPrivate ?? false,
            TrackCount = counts.TryGetValue(record.Id, out var count) ? count : 0,
            CreatedAt = record.CreatedAt,
            LikesCount = record.LikesCount
        }).ToList();
    }

    public async Task<PlaylistDetailResponse?> GetPlaylistDetailAsync(string playlistId, Guid viewerId, string accessToken, CancellationToken cancellationToken = default)
    {
        var playlist = await GetPlaylistRecordAsync(playlistId, accessToken, "user", cancellationToken);
        var usingFallbackBearer = false;

        if (playlist == null)
        {
            playlist = await GetPlaylistRecordAsync(playlistId, _key, "service", cancellationToken);
            usingFallbackBearer = playlist != null;
        }

        if (playlist == null)
        {
            return null;
        }

        if (playlist.IsPrivate == true && playlist.UserId != viewerId)
        {
            return null;
        }

        var ownerProfile = await GetProfileByIdAsync(playlist.UserId, accessToken) ?? await GetProfileByIdAsync(playlist.UserId, _key);
        if (ownerProfile == null)
        {
            Console.WriteLine($"[WARN] Owner profile {playlist.UserId} missing for playlist {playlistId}.");
            return null;
        }

        var bearer = usingFallbackBearer ? _key : accessToken;
        var entries = await GetPlaylistTracksAsync(playlistId, bearer, cancellationToken);
        var orderedEntries = entries
            .Where(entry => entry.Track != null)
            .OrderBy(entry => entry.Position ?? int.MaxValue)
            .ToList();

        var artistIds = orderedEntries
            .Select(entry => entry.Track!.UserId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var artists = artistIds.Any()
            ? await GetProfilesByIdsAsync(artistIds, accessToken)
            : new List<ProfileDto>();

        var artistLookup = artists.ToDictionary(artist => artist.Id);

        var trackSummaries = new List<TrackSummary>(orderedEntries.Count);
        foreach (var entry in orderedEntries)
        {
            var trackRecord = entry.Track!;
            artistLookup.TryGetValue(trackRecord.UserId, out var artistProfile);
            trackSummaries.Add(new TrackSummary
            {
                TrackId = trackRecord.Id,
                Title = trackRecord.Title,
                Plays = trackRecord.PlaysCount > int.MaxValue ? int.MaxValue : (int)trackRecord.PlaysCount,
                Likes = trackRecord.LikesCount > int.MaxValue ? int.MaxValue : (int)trackRecord.LikesCount,
                Artist = artistProfile?.FullName ?? artistProfile?.Username ?? string.Empty,
                ArtistId = artistProfile?.Id ?? trackRecord.UserId,
                ArtistAvatar = artistProfile?.AvatarUrl,
                CoverUrl = trackRecord.CoverUrl,
                AudioUrl = trackRecord.AudioUrl,
                DurationSeconds = trackRecord.DurationSeconds
            });
        }

        var summary = new PlaylistSummary
        {
            Id = playlist.Id,
            UserId = playlist.UserId,
            Title = playlist.Title,
            Description = playlist.Description,
            CoverUrl = playlist.CoverUrl,
            IsPrivate = playlist.IsPrivate ?? false,
            TrackCount = trackSummaries.Count,
            CreatedAt = playlist.CreatedAt,
            LikesCount = playlist.LikesCount
        };

        var isLiked = await IsPlaylistLikedAsync(viewerId, playlist.Id, accessToken);

        return new PlaylistDetailResponse
        {
            Playlist = summary,
            Owner = ownerProfile,
            Tracks = trackSummaries,
            IsOwner = playlist.UserId == viewerId,
            IsLiked = isLiked
        };
    }

    public async Task<PlaylistSummary?> CreatePlaylistAsync(Guid userId, PlaylistCreateRequest request, string accessToken, CancellationToken cancellationToken = default)
    {
        TrackDetailRecord? initialTrack = null;

        if (request.InitialTrackId.HasValue)
        {
            if (request.InitialTrackId.Value <= 0)
            {
                throw new HttpRequestException("An initial track id, when provided, must be positive.", null, HttpStatusCode.BadRequest);
            }

            initialTrack = await GetTrackDetailAsync(request.InitialTrackId.Value, userId, accessToken, cancellationToken);
            if (initialTrack == null)
            {
                throw new HttpRequestException("Initial track not found or inaccessible.", null, HttpStatusCode.NotFound);
            }
        }

        var coverToUse = string.IsNullOrWhiteSpace(request.CoverUrl)
            ? (initialTrack != null && !string.IsNullOrWhiteSpace(initialTrack.CoverUrl) ? initialTrack.CoverUrl : null)
            : request.CoverUrl;

        coverToUse = string.IsNullOrWhiteSpace(coverToUse) ? null : coverToUse.Trim();

        var payload = new Dictionary<string, object?>
        {
            ["user_id"] = userId,
            ["title"] = string.IsNullOrWhiteSpace(request.Title) ? "Untitled Playlist" : request.Title.Trim(),
            ["description"] = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
            ["cover_url"] = coverToUse,
            ["is_private"] = request.IsPrivate
        };

        var postRequest = CreateAuthRequest(HttpMethod.Post, "/rest/v1/playlists", payload, accessToken);
        postRequest.Headers.TryAddWithoutValidation("Prefer", "return=representation");

        using var response = await _httpClient.SendAsync(postRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to create playlist." : body, null, response.StatusCode);
        }

        var created = await response.Content.ReadFromJsonAsync<List<PlaylistRecord>>(_jsonOptions, cancellationToken) ?? new List<PlaylistRecord>();
        var record = created.FirstOrDefault();
        if (record == null)
        {
            return null;
        }

        if (initialTrack != null)
        {
            try
            {
                await AddTrackToPlaylistAsync(record.Id, userId, new PlaylistTrackAddRequest { TrackId = initialTrack.Id }, accessToken, cancellationToken);
            }
            catch
            {
                await TryDeletePlaylistAsync(record.Id, accessToken, cancellationToken);
                throw;
            }
        }

        return new PlaylistSummary
        {
            Id = record.Id,
            UserId = record.UserId,
            Title = record.Title,
            Description = record.Description,
            CoverUrl = record.CoverUrl ?? coverToUse,
            IsPrivate = record.IsPrivate ?? false,
            TrackCount = initialTrack != null ? 1 : 0,
            CreatedAt = record.CreatedAt,
            LikesCount = 0
        };
    }

    public async Task<PlaylistSummary?> UpdatePlaylistAsync(string playlistId, Guid userId, PlaylistUpdateRequest request, string accessToken, CancellationToken cancellationToken = default)
    {
        var ownerId = await GetPlaylistOwnerAsync(playlistId, cancellationToken);
        if (ownerId == null)
        {
            return null;
        }

        if (ownerId != userId)
        {
            throw new HttpRequestException("You do not have permission to update this playlist.", null, HttpStatusCode.Forbidden);
        }

        var payload = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            payload["title"] = request.Title.Trim();
        }

        if (request.Description != null)
        {
            payload["description"] = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description;
        }

        if (request.CoverUrl != null)
        {
            payload["cover_url"] = string.IsNullOrWhiteSpace(request.CoverUrl) ? null : request.CoverUrl;
        }

        if (request.IsPrivate.HasValue)
        {
            payload["is_private"] = request.IsPrivate.Value;
        }

        if (payload.Count == 0)
        {
            var existing = await GetPlaylistDetailAsync(playlistId, userId, accessToken, cancellationToken);
            return existing?.Playlist;
        }

        var encodedPlaylistId = Uri.EscapeDataString(playlistId);
        var patchRequest = CreateAuthRequest(HttpMethod.Patch, $"/rest/v1/playlists?id=eq.{encodedPlaylistId}", payload, accessToken);
        patchRequest.Headers.TryAddWithoutValidation("Prefer", "return=representation");

        using var response = await _httpClient.SendAsync(patchRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to update playlist." : body, null, response.StatusCode);
        }

        var updated = await response.Content.ReadFromJsonAsync<List<PlaylistRecord>>(_jsonOptions, cancellationToken) ?? new List<PlaylistRecord>();
        var record = updated.FirstOrDefault();
        if (record == null)
        {
            return null;
        }

        var count = await GetCountWithServiceKeyAsync($"/rest/v1/playlist_tracks?select=playlist_id&playlist_id=eq.{encodedPlaylistId}", cancellationToken);

        return new PlaylistSummary
        {
            Id = record.Id,
            UserId = record.UserId,
            Title = record.Title,
            Description = record.Description,
            CoverUrl = record.CoverUrl,
            IsPrivate = record.IsPrivate ?? false,
            TrackCount = count,
            CreatedAt = record.CreatedAt,
            LikesCount = record.LikesCount
        };
    }

    public async Task AddTrackToPlaylistAsync(string playlistId, Guid userId, PlaylistTrackAddRequest request, string accessToken, CancellationToken cancellationToken = default)
    {
        if (request.TrackId <= 0)
        {
            throw new HttpRequestException("Invalid track id.", null, HttpStatusCode.BadRequest);
        }

        var ownerId = await GetPlaylistOwnerAsync(playlistId, cancellationToken);
        if (ownerId == null)
        {
            throw new HttpRequestException("Playlist not found.", null, HttpStatusCode.NotFound);
        }

        if (ownerId != userId)
        {
            throw new HttpRequestException("You do not have permission to modify this playlist.", null, HttpStatusCode.Forbidden);
        }

        var encodedPlaylistId = Uri.EscapeDataString(playlistId);
        var existing = await GetCountWithServiceKeyAsync($"/rest/v1/playlist_tracks?select=playlist_id&playlist_id=eq.{encodedPlaylistId}&track_id=eq.{request.TrackId}", cancellationToken);
        if (existing > 0)
        {
            return;
        }

        var currentCount = await GetCountWithServiceKeyAsync($"/rest/v1/playlist_tracks?select=playlist_id&playlist_id=eq.{encodedPlaylistId}", cancellationToken);

        var payload = new Dictionary<string, object?>
        {
            ["playlist_id"] = playlistId,
            ["track_id"] = request.TrackId,
            ["position"] = currentCount + 1
        };

        var postRequest = CreateAuthRequest(HttpMethod.Post, "/rest/v1/playlist_tracks", payload, accessToken);
        postRequest.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");

        using var response = await _httpClient.SendAsync(postRequest, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to add track to playlist." : body, null, response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        await UpdatePlaylistCoverFromTracksAsync(playlistId, cancellationToken);
    }

    public async Task RemoveTrackFromPlaylistAsync(string playlistId, Guid userId, long trackId, string accessToken, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new HttpRequestException("Invalid track id.", null, HttpStatusCode.BadRequest);
        }

        var ownerId = await GetPlaylistOwnerAsync(playlistId, cancellationToken);
        if (ownerId == null)
        {
            throw new HttpRequestException("Playlist not found.", null, HttpStatusCode.NotFound);
        }

        if (ownerId != userId)
        {
            throw new HttpRequestException("You do not have permission to modify this playlist.", null, HttpStatusCode.Forbidden);
        }

        var encodedPlaylistId = Uri.EscapeDataString(playlistId);
        var deleteRequest = CreateAuthRequest(HttpMethod.Delete, $"/rest/v1/playlist_tracks?playlist_id=eq.{encodedPlaylistId}&track_id=eq.{trackId}", null, accessToken);
        deleteRequest.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        using var response = await _httpClient.SendAsync(deleteRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to remove track from playlist." : body, null, response.StatusCode);
        }

        await NormalizePlaylistTrackPositionsAsync(playlistId, cancellationToken);
        await UpdatePlaylistCoverFromTracksAsync(playlistId, cancellationToken);
    }

    public async Task DeletePlaylistAsync(string playlistId, Guid userId, string accessToken, CancellationToken cancellationToken = default)
    {
        var ownerId = await GetPlaylistOwnerAsync(playlistId, cancellationToken);
        if (ownerId == null)
        {
            throw new HttpRequestException("Playlist not found.", null, HttpStatusCode.NotFound);
        }

        if (ownerId != userId)
        {
            throw new HttpRequestException("You do not have permission to delete this playlist.", null, HttpStatusCode.Forbidden);
        }

        var encodedPlaylistId = Uri.EscapeDataString(playlistId);
        var deleteRequest = CreateAuthRequest(HttpMethod.Delete, $"/rest/v1/playlists?id=eq.{encodedPlaylistId}", null, accessToken);
        deleteRequest.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        using var response = await _httpClient.SendAsync(deleteRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? "Unable to delete playlist." : body, null, response.StatusCode);
        }
    }

    private async Task<PlaylistRecord?> GetPlaylistRecordAsync(string playlistId, string bearer, string mode, CancellationToken cancellationToken)
    {
        var encodedPlaylistId = Uri.EscapeDataString(playlistId);
        var path = $"/rest/v1/playlists?select=id,user_id,title,description,cover_url,is_private,created_at,likes_count&id=eq.{encodedPlaylistId}&limit=1";
        using var response = await _httpClient.SendAsync(CreateAuthRequest(HttpMethod.Get, path, null, bearer), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"[WARN] Playlist {playlistId} request via {mode} token failed with status {(int)response.StatusCode}: {response.StatusCode}. Body: {details}");
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<List<PlaylistRecord>>(_jsonOptions, cancellationToken) ?? new List<PlaylistRecord>();
        return payload.FirstOrDefault();
    }

    private async Task<List<PlaylistTrackRecord>> GetPlaylistTracksAsync(string playlistId, string bearer, CancellationToken cancellationToken)
    {
        var encodedPlaylistId = Uri.EscapeDataString(playlistId);
        var path = $"/rest/v1/playlist_tracks?select=playlist_id,track_id,position,track:tracks(id,title,plays_count,likes_count,user_id,audio_url,cover_url,duration_seconds)&playlist_id=eq.{encodedPlaylistId}&order=position.asc";
        using var response = await _httpClient.SendAsync(CreateAuthRequest(HttpMethod.Get, path, null, bearer), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<PlaylistTrackRecord>();
        }

        return await response.Content.ReadFromJsonAsync<List<PlaylistTrackRecord>>(_jsonOptions, cancellationToken) ?? new List<PlaylistTrackRecord>();
    }

    private async Task NormalizePlaylistTrackPositionsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var entries = await GetPlaylistTracksAsync(playlistId, _key, cancellationToken);
        var ordered = entries
            .Where(entry => entry.TrackId > 0)
            .OrderBy(entry => entry.Position ?? int.MaxValue)
            .ToList();

        var encodedPlaylistId = Uri.EscapeDataString(playlistId);

        for (var index = 0; index < ordered.Count; index++)
        {
            var desiredPosition = index + 1;
            if (ordered[index].Position == desiredPosition)
            {
                continue;
            }

            var payload = new Dictionary<string, object?>
            {
                ["position"] = desiredPosition
            };

            var patchRequest = CreateAuthRequest(HttpMethod.Patch, $"/rest/v1/playlist_tracks?playlist_id=eq.{encodedPlaylistId}&track_id=eq.{ordered[index].TrackId}", payload, _key);
            patchRequest.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

            using var response = await _httpClient.SendAsync(patchRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[WARN] Failed to normalize playlist track position for playlist {playlistId}, track {ordered[index].TrackId}: {response.StatusCode} {body}");
            }
        }
    }

    private async Task UpdatePlaylistCoverFromTracksAsync(string playlistId, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await GetPlaylistTracksAsync(playlistId, _key, cancellationToken);
            var firstCover = entries
                .OrderBy(entry => entry.Position ?? int.MaxValue)
                .Select(entry => entry.Track?.CoverUrl)
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

            var payload = new Dictionary<string, object?>
            {
                ["cover_url"] = string.IsNullOrWhiteSpace(firstCover) ? null : firstCover!.Trim()
            };

            var encodedPlaylistId = Uri.EscapeDataString(playlistId);
            var patchRequest = CreateAuthRequest(HttpMethod.Patch, $"/rest/v1/playlists?id=eq.{encodedPlaylistId}", payload, _key);
            patchRequest.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

            using var response = await _httpClient.SendAsync(patchRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[WARN] Failed to update cover for playlist {playlistId}: {response.StatusCode} {body}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Exception while updating cover for playlist {playlistId}: {ex.Message}");
        }
    }

    private async Task<Dictionary<string, int>> GetPlaylistTrackCountsAsync(IEnumerable<string> playlistIds, CancellationToken cancellationToken)
    {
        var idList = playlistIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

        if (!idList.Any())
        {
            return new Dictionary<string, int>();
        }

        // Ensure mixed identifier types stay valid in the PostgREST IN filter.
        var filter = string.Join(',', idList.Select(id =>
        {
            if (Guid.TryParse(id, out _))
            {
                return Uri.EscapeDataString(id);
            }

            var sanitized = id.Replace("\"", "\"\"");
            var quoted = $"\"{sanitized}\"";
            return Uri.EscapeDataString(quoted);
        }));

        using var request = CreateAuthRequest(HttpMethod.Get, $"/rest/v1/playlist_tracks?select=playlist_id&playlist_id=in.({filter})", null, _key);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new Dictionary<string, int>();
        }

        var payload = await response.Content.ReadFromJsonAsync<List<PlaylistIdRecord>>(_jsonOptions, cancellationToken) ?? new List<PlaylistIdRecord>();
        return payload
            .GroupBy(row => row.PlaylistId)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    private async Task<Guid?> GetPlaylistOwnerAsync(string playlistId, CancellationToken cancellationToken)
    {
        var record = await GetPlaylistRecordAsync(playlistId, _key, "ownership", cancellationToken);
        return record?.UserId;
    }

    private async Task TryDeletePlaylistAsync(string playlistId, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var encodedPlaylistId = Uri.EscapeDataString(playlistId);
            using var deleteRequest = CreateAuthRequest(HttpMethod.Delete, $"/rest/v1/playlists?id=eq.{encodedPlaylistId}", null, accessToken);
            deleteRequest.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

            using var response = await _httpClient.SendAsync(deleteRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var details = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[WARN] Failed to roll back playlist {playlistId}: {response.StatusCode} {details}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Exception while rolling back playlist {playlistId}: {ex.Message}");
        }
    }

    private sealed record TrackLikesCounter([property: JsonPropertyName("likes_count")] int LikesCount);
    private sealed record PlaylistLikesCounter([property: JsonPropertyName("likes_count")] int LikesCount);

    private sealed class FlexibleStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out var longValue))
                    {
                        return longValue.ToString(CultureInfo.InvariantCulture);
                    }

                    if (reader.TryGetDouble(out var doubleValue))
                    {
                        return doubleValue.ToString(CultureInfo.InvariantCulture);
                    }

                    return reader.GetDecimal().ToString(CultureInfo.InvariantCulture);
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.True:
                case JsonTokenType.False:
                    return reader.GetBoolean().ToString();
                default:
                    using (var document = JsonDocument.ParseValue(ref reader))
                    {
                        return document.RootElement.ToString();
                    }
            }
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value);
            }
        }
    }

    private async Task UpdateTrackLikesCountAsync(long trackId, int delta, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var getRequest = CreateAuthRequest(HttpMethod.Get, $"/rest/v1/tracks?select=likes_count&id=eq.{trackId}&limit=1");
            using var getResponse = await _httpClient.SendAsync(getRequest, cancellationToken);
            if (!getResponse.IsSuccessStatusCode)
            {
                return;
            }

            var counters = await getResponse.Content.ReadFromJsonAsync<List<TrackLikesCounter>>(_jsonOptions, cancellationToken);
            var current = counters?.FirstOrDefault()?.LikesCount ?? 0;
            var updated = Math.Max(0, current + delta);

            var payload = new Dictionary<string, object?>
            {
                ["likes_count"] = updated
            };

            var patchRequest = CreateAuthRequest(HttpMethod.Patch, $"/rest/v1/tracks?id=eq.{trackId}", payload);
            patchRequest.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

            using var patchResponse = await _httpClient.SendAsync(patchRequest, cancellationToken);
            if (!patchResponse.IsSuccessStatusCode)
            {
                var body = await patchResponse.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[WARN] Failed to update likes_count for track {trackId}: {patchResponse.StatusCode} {body}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Exception while updating likes_count for track {trackId}: {ex.Message}");
        }
    }

    private async Task UpdatePlaylistLikesCountAsync(string playlistId, int delta, CancellationToken cancellationToken)
    {
        try
        {
            var encodedPlaylistId = Uri.EscapeDataString(playlistId);
            var getRequest = CreateAuthRequest(HttpMethod.Get, $"/rest/v1/playlists?select=likes_count&id=eq.{encodedPlaylistId}&limit=1");
            using var getResponse = await _httpClient.SendAsync(getRequest, cancellationToken);
            if (!getResponse.IsSuccessStatusCode)
            {
                return;
            }

            var counters = await getResponse.Content.ReadFromJsonAsync<List<PlaylistLikesCounter>>(_jsonOptions, cancellationToken);
            var current = counters?.FirstOrDefault()?.LikesCount ?? 0;
            var updated = Math.Max(0, current + delta);

            var payload = new Dictionary<string, object?>
            {
                ["likes_count"] = updated
            };

            var patchRequest = CreateAuthRequest(HttpMethod.Patch, $"/rest/v1/playlists?id=eq.{encodedPlaylistId}", payload);
            patchRequest.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

            using var patchResponse = await _httpClient.SendAsync(patchRequest, cancellationToken);
            if (!patchResponse.IsSuccessStatusCode)
            {
                var body = await patchResponse.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[WARN] Failed to update likes_count for playlist {playlistId}: {patchResponse.StatusCode} {body}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Exception while updating likes_count for playlist {playlistId}: {ex.Message}");
        }
    }

    private async Task UpdateProfileSubscribeCountAsync(Guid profileId, int delta, string accessToken, CancellationToken cancellationToken)
    {
        var columns = new[] { "subscribe_count", "subcribe_count" };
        foreach (var column in columns)
        {
            if (await TryUpdateProfileCounterAsync(profileId, delta, accessToken, cancellationToken, column))
            {
                return;
            }
        }
    }

    private async Task<bool> TryUpdateProfileCounterAsync(Guid profileId, int delta, string accessToken, CancellationToken cancellationToken, string column)
    {
        try
        {
            var getRequest = CreateAuthRequest(HttpMethod.Get, $"/rest/v1/profiles?select={column}&id=eq.{profileId}&limit=1");
            using var getResponse = await _httpClient.SendAsync(getRequest, cancellationToken);
            if (!getResponse.IsSuccessStatusCode)
            {
                return false;
            }

            var document = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(document) ? "[]" : document);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
            {
                return false;
            }

            var item = json.RootElement[0];
            if (!item.TryGetProperty(column, out var valueElement))
            {
                return false;
            }

            var current = valueElement.ValueKind switch
            {
                JsonValueKind.Number when valueElement.TryGetInt32(out var number) => number,
                JsonValueKind.Number when valueElement.TryGetInt64(out var longNumber) => (int)Math.Clamp(longNumber, int.MinValue, int.MaxValue),
                _ => 0
            };

            var updated = Math.Max(0, current + delta);

            var payload = new Dictionary<string, object?>
            {
                [column] = updated
            };

            var patchRequest = CreateAuthRequest(HttpMethod.Patch, $"/rest/v1/profiles?id=eq.{profileId}", payload);
            patchRequest.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

            using var patchResponse = await _httpClient.SendAsync(patchRequest, cancellationToken);
            if (patchResponse.IsSuccessStatusCode)
            {
                return true;
            }

            var body = await patchResponse.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"[WARN] Failed to update {column} for profile {profileId}: {patchResponse.StatusCode} {body}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Exception while updating {column} for profile {profileId}: {ex.Message}");
            return false;
        }
    }

    public async Task<TrackRecord?> CreateTrackRecordAsync(TrackInsertPayload payload, string accessToken, CancellationToken cancellationToken = default)
    {
        if (payload == null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        if (string.IsNullOrWhiteSpace(payload.AudioUrl))
        {
            throw new ArgumentException("Audio URL is required for track creation.", nameof(payload.AudioUrl));
        }

        if (string.IsNullOrWhiteSpace(payload.Title))
        {
            throw new ArgumentException("Track title is required for track creation.", nameof(payload.Title));
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Access token is required for track creation.", nameof(accessToken));
        }

        await EnsureInitializedAsync();

        var request = CreateAuthRequest(
            HttpMethod.Post,
            "/rest/v1/tracks?select=id,title,plays_count,likes_count,user_id,audio_url,cover_url",
            new[] { payload },
            accessToken);

        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(errorBody) ? "Failed to create track record." : errorBody,
                null,
                response.StatusCode);
        }

        var records = await response.Content.ReadFromJsonAsync<List<TrackRecord>>(_jsonOptions, cancellationToken)
                      ?? new List<TrackRecord>();

        if (records.Count == 0)
        {
            throw new HttpRequestException("Supabase did not return the created track record.", null, HttpStatusCode.InternalServerError);
        }

        return records[0];
    }


    public async Task<StorageUploadResult> UploadTrackAsync(Stream stream, string storagePath, string? contentType, string accessToken, CancellationToken cancellationToken = default)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (string.IsNullOrWhiteSpace(storagePath))
        {
            throw new ArgumentException("Storage path must be provided.", nameof(storagePath));
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Access token is required for storage upload.", nameof(accessToken));
        }

        await EnsureInitializedAsync();

        var mediaType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        var encodedPath = string.Join('/', storagePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        string? userError = null;
        var response = await UploadToStorageAsync(stream, encodedPath, mediaType, accessToken, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode == HttpStatusCode.Forbidden)
        {
            userError = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();

            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            response = await UploadToStorageAsync(stream, encodedPath, mediaType, _key, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var serviceError = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                throw new HttpRequestException(
                    string.IsNullOrWhiteSpace(serviceError)
                        ? string.IsNullOrWhiteSpace(userError) ? "Supabase storage upload failed." : userError
                        : serviceError,
                    null,
                    response.StatusCode);
            }
        }
        else if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(errorBody)
                    ? $"Supabase storage upload failed with status {(int)response.StatusCode}."
                    : errorBody,
                null,
                response.StatusCode);
        }

        response.Dispose();

        var publicUrl = $"{_url}/storage/v1/object/public/tracks/{encodedPath}";

        return new StorageUploadResult(storagePath, publicUrl);
    }

    private async Task<HttpResponseMessage> UploadToStorageAsync(Stream stream, string encodedPath, string mediaType, string bearer, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        var content = new NonClosingStreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/storage/v1/object/tracks/{encodedPath}")
        {
            Content = content
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Remove("x-upsert");
        request.Headers.TryAddWithoutValidation("x-upsert", "true");

        return await _httpClient.SendAsync(request, cancellationToken);
    }
}

public record StorageUploadResult(string Path, string PublicUrl);

file sealed class NonClosingStreamContent : StreamContent
{
    public NonClosingStreamContent(Stream stream) : base(stream)
    {
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(false);
    }
}

