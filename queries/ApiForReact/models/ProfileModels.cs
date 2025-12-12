using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace queries.models;

public record SupabaseUser
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }
}

public record ProfileDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string? FullName { get; init; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; init; }

    [JsonPropertyName("likes_count")]
    public long LikesCount { get; init; }

    [JsonPropertyName("banner_url")]
    public string? BannerUrl { get; init; }

    [JsonPropertyName("bio")]
    public string? Bio { get; init; }

    [JsonPropertyName("is_following")]
    public bool IsFollowing { get; init; }
}

public record ProfileUpsertRequest
{
    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("fullName")]
    public string? FullName { get; init; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; init; }

    [JsonPropertyName("bio")]
    public string? Bio { get; init; }

    [JsonPropertyName("bannerUrl")]
    public string? BannerUrl { get; init; }
}

public record ProfileStats(int Followers, int Following, int Tracks, int Likes);

public record TrackSummary
{
    public long TrackId { get; init; }
    public string Title { get; init; } = string.Empty;
    public int Plays { get; init; }
    public int Likes { get; init; }
    public string Artist { get; init; } = string.Empty;
    public Guid ArtistId { get; init; }
    public string? ArtistAvatar { get; init; }
    public string? AudioUrl { get; init; }
    public string? CoverUrl { get; init; }
    public int? DurationSeconds { get; init; }
    public DateTimeOffset? PlayedAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
}

public record SimpleProfile
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string? AvatarUrl { get; init; }
    public int Followers { get; init; }
    public int Tracks { get; init; }
    [JsonPropertyName("banner_url")]
    public string? BannerUrl { get; init; }
}

public record RecommendedArtist
{
    public Guid Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string? AvatarUrl { get; init; }
    public int Followers { get; init; }
    public int Tracks { get; init; }
    public bool IsFollowing { get; init; }
}

public record ProfileViewModel
{
    [JsonPropertyName("profile")]
    public ProfileDto Profile { get; init; } = null!;
    
    [JsonPropertyName("stats")]
    public ProfileStats Stats { get; init; } = null!;
    
    [JsonPropertyName("tracks")]
    public List<TrackSummary> Tracks { get; init; } = new();
    
    [JsonPropertyName("likes")]
    public List<TrackSummary> Likes { get; init; } = new();
    
    [JsonPropertyName("playlists")]
    public List<PlaylistSummary> Playlists { get; init; } = new();

    [JsonPropertyName("liked_playlists")]
    public List<PlaylistSummary> LikedPlaylists { get; init; } = new();

    [JsonPropertyName("following")]
    public List<SimpleProfile> Following { get; init; } = new();
}

public record HomeSidebarViewModel
{
    [JsonPropertyName("recommended")]
    public List<RecommendedArtist> Recommended { get; init; } = new();

    [JsonPropertyName("likes")]
    public List<TrackSummary> Likes { get; init; } = new();

    [JsonPropertyName("history")]
    public List<TrackSummary> History { get; init; } = new();
}

public record TrackRecord
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("plays_count")]
    public long PlaysCount { get; init; }

    [JsonPropertyName("likes_count")]
    public long LikesCount { get; init; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; init; }

    [JsonPropertyName("audio_url")]
    public string? AudioUrl { get; init; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("duration_seconds")]
    public int? DurationSeconds { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }
}

public record TrackLikeRecord
{
    [JsonPropertyName("track")]
    public TrackRecord Track { get; init; } = null!;
}

public record PlaylistLikeRecord
{
    [JsonPropertyName("playlist")]
    public PlaylistRecord Playlist { get; init; } = null!;
}

public record ListeningHistoryRecord
{
    [JsonPropertyName("played_at")]
    public DateTimeOffset? PlayedAt { get; init; }

    [JsonPropertyName("track")]
    public TrackRecord? Track { get; init; }
}

public record FollowingRelation
{
    [JsonPropertyName("following_id")]
    public Guid FollowingId { get; init; }
}

public record FollowerCount
{
    [JsonPropertyName("following_id")]
    public Guid FollowingId { get; init; }
}

public record TrackCountRecord
{
    [JsonPropertyName("user_id")]
    public Guid UserId { get; init; }
}

public record UserIdRecord
{
    [JsonPropertyName("user_id")]
    public Guid UserId { get; init; }
}

public record TrackSearchResult
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("user_id")]
    public Guid UserId { get; init; }
}

public record TrackInsertPayload
{
    [JsonPropertyName("user_id")]
    public Guid UserId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("audio_url")]
    public string AudioUrl { get; init; } = string.Empty;

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("duration_seconds")]
    public int? DurationSeconds { get; init; }

    [JsonPropertyName("is_private")]
    public bool? IsPrivate { get; init; }
}

public record TrackDetailRecord
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("audio_url")]
    public string AudioUrl { get; init; } = string.Empty;

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("waveform_data")]
    public string? WaveformData { get; init; }

    [JsonPropertyName("duration_seconds")]
    public int? DurationSeconds { get; init; }

    [JsonPropertyName("plays_count")]
    public long PlaysCount { get; init; }

    [JsonPropertyName("likes_count")]
    public long LikesCount { get; init; }

    [JsonPropertyName("is_private")]
    public bool? IsPrivate { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("uploaded_at")]
    public DateTimeOffset? UploadedAt { get; init; }
}

public record TrackDetailResponse(TrackDetailRecord Track, ProfileDto Artist, bool IsLiked, bool IsFollowing);

public record PlaylistRecord
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("user_id")]
    public Guid UserId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("is_private")]
    public bool? IsPrivate { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("likes_count")]
    public long LikesCount { get; init; }
}

public record PlaylistSummary
{
    public string Id { get; init; } = string.Empty;
    public Guid UserId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? CoverUrl { get; init; }
    public string? FirstTrackCoverUrl { get; init; }
    public bool IsPrivate { get; init; }
    public int TrackCount { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public long LikesCount { get; init; }
}

public record PlaylistTrackRecord
{
    [JsonPropertyName("playlist_id")]
    public string PlaylistId { get; init; } = string.Empty;

    [JsonPropertyName("track_id")]
    public long TrackId { get; init; }

    [JsonPropertyName("position")]
    public int? Position { get; init; }

    [JsonPropertyName("track")]
    public TrackRecord? Track { get; init; }
}

public record PlaylistDetailResponse
{
    [JsonPropertyName("playlist")]
    public PlaylistSummary Playlist { get; init; } = null!;

    [JsonPropertyName("owner")]
    public ProfileDto Owner { get; init; } = null!;

    [JsonPropertyName("tracks")]
    public List<TrackSummary> Tracks { get; init; } = new();

    [JsonPropertyName("isOwner")]
    public bool IsOwner { get; init; }

    [JsonPropertyName("isLiked")]
    public bool IsLiked { get; init; }
}

public record PlaylistCreateRequest
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; init; }

    [JsonPropertyName("initialTrackId")]
    public long? InitialTrackId { get; init; }
}

public record PlaylistUpdateRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("isPrivate")]
    public bool? IsPrivate { get; init; }
}

public record PlaylistTrackAddRequest
{
    [JsonPropertyName("trackId")]
    public long TrackId { get; init; }
}

public record PlaylistIdRecord
{
    [JsonPropertyName("playlist_id")]
    public string PlaylistId { get; init; } = string.Empty;
}

public record FeedOwner
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; init; }
}

public record FeedItem
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("owner")]
    public FeedOwner Owner { get; init; } = null!;

    [JsonPropertyName("track")]
    public TrackSummary? Track { get; init; }

    [JsonPropertyName("playlist")]
    public PlaylistSummary? Playlist { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }
}

public record FeedResponse
{
    [JsonPropertyName("items")]
    public List<FeedItem> Items { get; init; } = new();
}
