using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using queries.models;

namespace queries.services;

public class ProfileService
{
    private readonly SupabaseService _supabase;

    public ProfileService(SupabaseService supabase)
    {
        _supabase = supabase;
    }

    public async Task<ProfileViewModel?> BuildProfileAsync(string accessToken)
    {
        var user = await _supabase.GetUserAsync(accessToken);
        if (user == null)
        {
            return null;
        }

        var profile = await _supabase.GetProfileByIdAsync(user.Id, accessToken);
        if (profile == null)
        {
            return null;
        }

        profile = profile with { IsFollowing = false };

        var trackRecords = await _supabase.GetTracksAsync(user.Id, accessToken);
        var playlists = await _supabase.GetPlaylistsAsync(user.Id, user.Id, accessToken);
        var likedPlaylists = await _supabase.GetLikedPlaylistsAsync(user.Id, accessToken);
        var likes = await _supabase.GetLikedTracksAsync(user.Id, accessToken);
        var relations = await _supabase.GetFollowingRelationsAsync(user.Id, accessToken);
        var followingIds = relations.Select(r => r.FollowingId).Distinct().ToList();

        var followingProfiles = await _supabase.GetProfilesByIdsAsync(followingIds, accessToken);
        var followerCounts = await _supabase.GetFollowerCountsAsync(followingIds, accessToken);
        var trackCounts = await _supabase.GetTracksCountsForUsersAsync(followingIds, accessToken);

        var artistProfiles = await _supabase.GetProfilesByIdsAsync(
            likes.Select(l => l.Track.UserId).Distinct(),
            accessToken);

        var artistLookup = artistProfiles.ToDictionary(p => p.Id);
        var followerLookup = followerCounts
            .GroupBy(f => f.FollowingId)
            .ToDictionary(g => g.Key, g => g.Count());
        var trackLookup = trackCounts
            .GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => g.Count());

        var profileTracks = trackRecords.Select(record => new TrackSummary
        {
            TrackId = record.Id,
            Title = record.Title,
            Plays = record.PlaysCount > int.MaxValue ? int.MaxValue : (int)record.PlaysCount,
            Likes = record.LikesCount > int.MaxValue ? int.MaxValue : (int)record.LikesCount,
            Artist = profile.FullName ?? profile.Username,
            ArtistId = profile.Id,
            ArtistAvatar = profile.AvatarUrl,
            CoverUrl = record.CoverUrl,
            AudioUrl = record.AudioUrl,
            DurationSeconds = record.DurationSeconds
        }).ToList();

        var likedTracks = likes.Select(record =>
        {
            artistLookup.TryGetValue(record.Track.UserId, out var artistProfile);
            return new TrackSummary
            {
                TrackId = record.Track.Id,
                Title = record.Track.Title,
                Plays = record.Track.PlaysCount > int.MaxValue ? int.MaxValue : (int)record.Track.PlaysCount,
                Likes = record.Track.LikesCount > int.MaxValue ? int.MaxValue : (int)record.Track.LikesCount,
                Artist = artistProfile?.FullName ?? artistProfile?.Username ?? string.Empty,
                ArtistId = artistProfile?.Id ?? Guid.Empty,
                ArtistAvatar = artistProfile?.AvatarUrl,
                CoverUrl = record.Track.CoverUrl,
                AudioUrl = record.Track.AudioUrl,
                DurationSeconds = record.Track.DurationSeconds
            };
        }).ToList();

        var profileLookup = followingProfiles.ToDictionary(p => p.Id);
        var orderedFollowing = followingIds
            .Where(id => profileLookup.ContainsKey(id))
            .Select(id => profileLookup[id])
            .ToList();

        var followingList = orderedFollowing.Select(profileRecord => new SimpleProfile
        {
            Id = profileRecord.Id,
            Username = profileRecord.Username,
            AvatarUrl = profileRecord.AvatarUrl,
            Followers = followerLookup.TryGetValue(profileRecord.Id, out var followerTotal) ? followerTotal : 0,
            Tracks = trackLookup.TryGetValue(profileRecord.Id, out var trackTotal) ? trackTotal : 0
        }).ToList();

        var followersCountTask = _supabase.GetFollowersCountAsync(user.Id, accessToken);
        var followingCountTask = _supabase.GetFollowingCountAsync(user.Id, accessToken);
        var tracksCountTask = _supabase.GetTracksCountAsync(user.Id, accessToken);
        var likesCountTask = _supabase.GetLikesCountAsync(user.Id, accessToken);

        var stats = new ProfileStats(
            Followers: await followersCountTask,
            Following: await followingCountTask,
            Tracks: await tracksCountTask,
            Likes: await likesCountTask
        );

        return new ProfileViewModel
        {
            Profile = profile,
            Stats = stats,
            Tracks = profileTracks,
            Playlists = playlists,
            LikedPlaylists = likedPlaylists,
            Likes = likedTracks,
            Following = followingList
        };
    }

    public async Task<ProfileViewModel?> BuildProfileByUsernameAsync(string username, Guid viewerId, string accessToken)
    {
        var profile = await _supabase.GetProfileByUsernameAsync(username, accessToken);
        if (profile == null)
        {
            return null;
        }

        var userId = profile.Id;

        var viewerFollows = viewerId != Guid.Empty && viewerId != userId
            ? await _supabase.IsFollowingAsync(viewerId, userId, accessToken)
            : false;

        profile = profile with { IsFollowing = viewerFollows };

        var trackRecords = await _supabase.GetTracksAsync(userId, accessToken);
        var likes = await _supabase.GetLikedTracksAsync(userId, accessToken);
        var playlists = await _supabase.GetPlaylistsAsync(userId, viewerId, accessToken);
        var likedPlaylists = await _supabase.GetLikedPlaylistsAsync(userId, accessToken);
        var relations = await _supabase.GetFollowingRelationsAsync(userId, accessToken);
        var followingIds = relations.Select(r => r.FollowingId).Distinct().ToList();

        var followingProfiles = await _supabase.GetProfilesByIdsAsync(followingIds, accessToken);
        var followerCounts = await _supabase.GetFollowerCountsAsync(followingIds, accessToken);
        var trackCounts = await _supabase.GetTracksCountsForUsersAsync(followingIds, accessToken);

        var artistProfiles = await _supabase.GetProfilesByIdsAsync(
            likes.Select(l => l.Track.UserId).Distinct(),
            accessToken);

        var artistLookup = artistProfiles.ToDictionary(p => p.Id);
        var followerLookup = followerCounts
            .GroupBy(f => f.FollowingId)
            .ToDictionary(g => g.Key, g => g.Count());
        var trackLookup = trackCounts
            .GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => g.Count());

        var profileTracks = trackRecords.Select(record => new TrackSummary
        {
            TrackId = record.Id,
            Title = record.Title,
            Plays = record.PlaysCount > int.MaxValue ? int.MaxValue : (int)record.PlaysCount,
            Likes = record.LikesCount > int.MaxValue ? int.MaxValue : (int)record.LikesCount,
            Artist = profile.FullName ?? profile.Username,
            ArtistId = profile.Id,
            ArtistAvatar = profile.AvatarUrl,
            CoverUrl = record.CoverUrl,
            AudioUrl = record.AudioUrl,
            DurationSeconds = record.DurationSeconds
        }).ToList();

        var likedTracks = likes.Select(record =>
        {
            artistLookup.TryGetValue(record.Track.UserId, out var artistProfile);
            return new TrackSummary
            {
                TrackId = record.Track.Id,
                Title = record.Track.Title,
                Plays = record.Track.PlaysCount > int.MaxValue ? int.MaxValue : (int)record.Track.PlaysCount,
                Likes = record.Track.LikesCount > int.MaxValue ? int.MaxValue : (int)record.Track.LikesCount,
                Artist = artistProfile?.FullName ?? artistProfile?.Username ?? string.Empty,
                ArtistId = artistProfile?.Id ?? Guid.Empty,
                ArtistAvatar = artistProfile?.AvatarUrl,
                CoverUrl = record.Track.CoverUrl,
                AudioUrl = record.Track.AudioUrl,
                DurationSeconds = record.Track.DurationSeconds
            };
        }).ToList();

        var profileLookup = followingProfiles.ToDictionary(p => p.Id);
        var orderedFollowing = followingIds
            .Where(id => profileLookup.ContainsKey(id))
            .Select(id => profileLookup[id])
            .ToList();

        var followingList = orderedFollowing.Select(profileRecord => new SimpleProfile
        {
            Id = profileRecord.Id,
            Username = profileRecord.Username,
            AvatarUrl = profileRecord.AvatarUrl,
            Followers = followerLookup.TryGetValue(profileRecord.Id, out var followerTotal) ? followerTotal : 0,
            Tracks = trackLookup.TryGetValue(profileRecord.Id, out var trackTotal) ? trackTotal : 0
        }).ToList();

        var followersCountTask = _supabase.GetFollowersCountAsync(userId, accessToken);
        var followingCountTask = _supabase.GetFollowingCountAsync(userId, accessToken);
        var tracksCountTask = _supabase.GetTracksCountAsync(userId, accessToken);
        var likesCountTask = _supabase.GetLikesCountAsync(userId, accessToken);

        var stats = new ProfileStats(
            Followers: await followersCountTask,
            Following: await followingCountTask,
            Tracks: await tracksCountTask,
            Likes: await likesCountTask
        );

        return new ProfileViewModel
        {
            Profile = profile,
            Stats = stats,
            Tracks = profileTracks,
            Playlists = playlists,
            LikedPlaylists = likedPlaylists,
            Likes = likedTracks,
            Following = followingList
        };
    }
}

