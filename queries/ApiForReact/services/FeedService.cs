using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using queries.models;

namespace queries.services;

public class FeedService
{
    private readonly SupabaseService _supabase;

    public FeedService(SupabaseService supabase)
    {
        _supabase = supabase;
    }

    public async Task<FeedResponse> BuildFeedAsync(Guid userId, string accessToken, CancellationToken cancellationToken = default)
    {
        var followingIds = await _supabase.GetFollowingIdsAsync(userId, accessToken, 96);
        if (followingIds.Count == 0)
        {
            return new FeedResponse();
        }

        var tracksTask = _supabase.GetLatestTracksForUsersAsync(followingIds, 40, accessToken, cancellationToken);
        var playlistsTask = _supabase.GetLatestPlaylistsForUsersAsync(followingIds, 40, accessToken, cancellationToken);

        await Task.WhenAll(tracksTask, playlistsTask);

        var trackRecords = tracksTask.Result;
        var playlistSummaries = playlistsTask.Result;

        var ownerIds = trackRecords
            .Select(track => track.UserId)
            .Concat(playlistSummaries.Select(playlist => playlist.UserId))
            .Distinct()
            .ToList();

        var profiles = ownerIds.Count > 0
            ? await _supabase.GetProfilesByIdsAsync(ownerIds, accessToken)
            : new List<ProfileDto>();

        var profileLookup = profiles.ToDictionary(profile => profile.Id);

        var items = new List<FeedItem>();

        foreach (var track in trackRecords)
        {
            if (!profileLookup.TryGetValue(track.UserId, out var profile))
            {
                continue;
            }

            var displayName = string.IsNullOrWhiteSpace(profile.FullName) ? profile.Username : profile.FullName!;

            var summary = new TrackSummary
            {
                TrackId = track.Id,
                Title = track.Title,
                Plays = track.PlaysCount > int.MaxValue ? int.MaxValue : (int)track.PlaysCount,
                Likes = track.LikesCount > int.MaxValue ? int.MaxValue : (int)track.LikesCount,
                Artist = displayName,
                ArtistId = profile.Id,
                ArtistAvatar = profile.AvatarUrl,
                AudioUrl = track.AudioUrl,
                CoverUrl = track.CoverUrl,
                DurationSeconds = track.DurationSeconds,
                CreatedAt = track.CreatedAt
            };

            items.Add(new FeedItem
            {
                Type = "track",
                CreatedAt = track.CreatedAt,
                Owner = new FeedOwner
                {
                    Id = profile.Id,
                    Username = profile.Username,
                    DisplayName = displayName,
                    AvatarUrl = profile.AvatarUrl
                },
                Track = summary
            });
        }

        foreach (var playlist in playlistSummaries)
        {
            if (!profileLookup.TryGetValue(playlist.UserId, out var profile))
            {
                continue;
            }

            var displayName = string.IsNullOrWhiteSpace(profile.FullName) ? profile.Username : profile.FullName!;

            items.Add(new FeedItem
            {
                Type = "playlist",
                CreatedAt = playlist.CreatedAt,
                Owner = new FeedOwner
                {
                    Id = profile.Id,
                    Username = profile.Username,
                    DisplayName = displayName,
                    AvatarUrl = profile.AvatarUrl
                },
                Playlist = playlist
            });
        }

        items.Sort((left, right) =>
        {
            var leftTime = left.CreatedAt ?? DateTimeOffset.MinValue;
            var rightTime = right.CreatedAt ?? DateTimeOffset.MinValue;
            return rightTime.CompareTo(leftTime);
        });

        return new FeedResponse
        {
            Items = items
        };
    }
}
