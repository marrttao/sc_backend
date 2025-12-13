using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using queries.models;

namespace queries.services;

public class HomeSidebarService
{
    private readonly SupabaseService _supabase;

    public HomeSidebarService(SupabaseService supabase)
    {
        _supabase = supabase;
    }

    public async Task<HomeSidebarViewModel> BuildSidebarAsync(Guid userId, string accessToken)
    {
        var recommendedTask = BuildRecommendedAsync(accessToken);
        var randomTracksTask = GetRandomTracksAsync(10, accessToken);
        var historyTask = BuildHistoryAsync(userId, accessToken);

        await Task.WhenAll(recommendedTask, randomTracksTask, historyTask);

        return new HomeSidebarViewModel
        {
            Recommended = recommendedTask.Result,
            Likes = randomTracksTask.Result,
            History = historyTask.Result
        };
    }

    private async Task<List<RecommendedArtist>> BuildRecommendedAsync(string accessToken)
    {
        // Получаем всех пользователей с треками (без фильтрации по userId)
        var candidateIds = await _supabase.GetRecentTrackOwnerIdsAsync(128, accessToken);
        var uniqueCandidates = candidateIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (uniqueCandidates.Count == 0)
        {
            return new List<RecommendedArtist>();
        }

        Shuffle(uniqueCandidates);
        var selectedIds = uniqueCandidates.Take(5).ToList();

        var profiles = await _supabase.GetProfilesByIdsAsync(selectedIds, accessToken);
        if (profiles.Count == 0)
        {
            return new List<RecommendedArtist>();
        }

        var profileLookup = profiles.ToDictionary(p => p.Id);
        var followerCounts = await _supabase.GetFollowerCountsAsync(selectedIds, accessToken);
        var trackCounts = await _supabase.GetTracksCountsForUsersAsync(selectedIds, accessToken);

        var followerLookup = followerCounts
            .GroupBy(f => f.FollowingId)
            .ToDictionary(g => g.Key, g => g.Count());

        var trackLookup = trackCounts
            .GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<RecommendedArtist>();
        foreach (var id in selectedIds)
        {
            if (!profileLookup.TryGetValue(id, out var profile))
            {
                continue;
            }

            result.Add(new RecommendedArtist
            {
                Id = profile.Id,
                DisplayName = string.IsNullOrWhiteSpace(profile.FullName) ? profile.Username : profile.FullName,
                Username = profile.Username,
                AvatarUrl = profile.AvatarUrl,
                Followers = followerLookup.TryGetValue(profile.Id, out var followers) ? followers : 0,
                Tracks = trackLookup.TryGetValue(profile.Id, out var tracks) ? tracks : 0,
                IsFollowing = false // неважно для карусели
            });
        }

        return result;
    }

    // Метод для случайных треков
    private async Task<List<TrackSummary>> GetRandomTracksAsync(int count, string accessToken)
    {
        var recentOwners = await _supabase.GetRecentTrackOwnerIdsAsync(128, accessToken);
        var tracks = await _supabase.GetLatestTracksForUsersAsync(recentOwners, 100, accessToken);
        Shuffle(tracks);
        var selected = tracks.Take(count).ToList();

        var artistIds = selected.Select(t => t.UserId).Distinct().ToList();
        var artistProfiles = await _supabase.GetProfilesByIdsAsync(artistIds, accessToken);
        var artistLookup = artistProfiles.ToDictionary(p => p.Id);

        return selected.Select(track =>
        {
            artistLookup.TryGetValue(track.UserId, out var artistProfile);
            return new TrackSummary
            {
                TrackId = track.Id,
                Title = track.Title,
                Plays = track.PlaysCount > int.MaxValue ? int.MaxValue : (int)track.PlaysCount,
                Likes = track.LikesCount > int.MaxValue ? int.MaxValue : (int)track.LikesCount,
                Artist = artistProfile?.FullName ?? artistProfile?.Username ?? string.Empty,
                ArtistId = artistProfile?.Id ?? Guid.Empty,
                ArtistAvatar = artistProfile?.AvatarUrl,
                CoverUrl = track.CoverUrl,
                AudioUrl = track.AudioUrl,
                DurationSeconds = track.DurationSeconds
            };
        }).ToList();
    }

    private async Task<List<TrackSummary>> BuildLikesAsync(Guid userId, string accessToken)
    {
        var likes = await _supabase.GetLikedTracksAsync(userId, accessToken);
        if (likes.Count == 0)
        {
            return new List<TrackSummary>();
        }

        var artistIds = likes
            .Select(like => like.Track.UserId)
            .Distinct()
            .ToList();

        var artistProfiles = await _supabase.GetProfilesByIdsAsync(artistIds, accessToken);
        var artistLookup = artistProfiles.ToDictionary(p => p.Id);

        return likes.Select(like =>
        {
            artistLookup.TryGetValue(like.Track.UserId, out var artistProfile);
            return new TrackSummary
            {
                TrackId = like.Track.Id,
                Title = like.Track.Title,
                Plays = like.Track.PlaysCount > int.MaxValue ? int.MaxValue : (int)like.Track.PlaysCount,
                Likes = like.Track.LikesCount > int.MaxValue ? int.MaxValue : (int)like.Track.LikesCount,
                Artist = artistProfile?.FullName ?? artistProfile?.Username ?? string.Empty,
                ArtistId = artistProfile?.Id ?? Guid.Empty,
                ArtistAvatar = artistProfile?.AvatarUrl,
                CoverUrl = like.Track.CoverUrl,
                AudioUrl = like.Track.AudioUrl,
                DurationSeconds = like.Track.DurationSeconds
            };
        }).ToList();
    }

    private async Task<List<TrackSummary>> BuildHistoryAsync(Guid userId, string accessToken)
    {
        var history = await _supabase.GetListeningHistoryAsync(userId, accessToken);
        if (history.Count == 0)
        {
            return new List<TrackSummary>();
        }

        var artistIds = history
            .Where(entry => entry.Track != null)
            .Select(entry => entry.Track!.UserId)
            .Distinct()
            .ToList();

        var artistProfiles = await _supabase.GetProfilesByIdsAsync(artistIds, accessToken);
        var artistLookup = artistProfiles.ToDictionary(p => p.Id);

        return history
            .Where(entry => entry.Track != null)
            .Select(entry =>
            {
                var track = entry.Track!;
                artistLookup.TryGetValue(track.UserId, out var artistProfile);
                return new TrackSummary
                {
                    TrackId = track.Id,
                    Title = track.Title,
                    Plays = track.PlaysCount > int.MaxValue ? int.MaxValue : (int)track.PlaysCount,
                    Likes = track.LikesCount > int.MaxValue ? int.MaxValue : (int)track.LikesCount,
                    Artist = artistProfile?.FullName ?? artistProfile?.Username ?? string.Empty,
                    ArtistId = artistProfile?.Id ?? Guid.Empty,
                    ArtistAvatar = artistProfile?.AvatarUrl,
                    CoverUrl = track.CoverUrl,
                    AudioUrl = track.AudioUrl,
                    DurationSeconds = track.DurationSeconds,
                    PlayedAt = entry.PlayedAt
                };
            })
            .ToList();
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using queries.models;

namespace queries.services;

public class HomeSidebarService
{
    private readonly SupabaseService _supabase;

    public HomeSidebarService(SupabaseService supabase)
    {
        _supabase = supabase;
    }

    public async Task<HomeSidebarViewModel> BuildSidebarAsync(Guid userId, string accessToken)
    {
        var recommendedTask = BuildRecommendedAsync(userId, accessToken);
        var likesTask = BuildLikesAsync(userId, accessToken);
        var historyTask = BuildHistoryAsync(userId, accessToken);
        var randomTracksTask = GetRandomTracksAsync(10, accessToken);

        await Task.WhenAll(recommendedTask, likesTask, historyTask, randomTracksTask);

        return new HomeSidebarViewModel
        {
            Recommended = recommendedTask.Result,
            Likes = randomTracksTask.Result, // теперь это случайные треки
            History = historyTask.Result
        };
    }

    private async Task<List<RecommendedArtist>> BuildRecommendedAsync(Guid userId, string accessToken)
    {
        // Получаем всех пользователей с треками
        var candidateIds = await _supabase.GetRecentTrackOwnerIdsAsync(128, accessToken);
        var uniqueCandidates = candidateIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (uniqueCandidates.Count == 0)
        {
            return new List<RecommendedArtist>();
        }

        Shuffle(uniqueCandidates);
        var selectedIds = uniqueCandidates.Take(5).ToList();

        var profiles = await _supabase.GetProfilesByIdsAsync(selectedIds, accessToken);
        if (profiles.Count == 0)
        {
            return new List<RecommendedArtist>();
        }

        var profileLookup = profiles.ToDictionary(p => p.Id);
        var followerCounts = await _supabase.GetFollowerCountsAsync(selectedIds, accessToken);
        var trackCounts = await _supabase.GetTracksCountsForUsersAsync(selectedIds, accessToken);

        var followerLookup = followerCounts
            .GroupBy(f => f.FollowingId)
            .ToDictionary(g => g.Key, g => g.Count());

        var trackLookup = trackCounts
            .GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<RecommendedArtist>();
        foreach (var id in selectedIds)
        {
            if (!profileLookup.TryGetValue(id, out var profile))
            {
                continue;
            }

            result.Add(new RecommendedArtist
            {
                Id = profile.Id,
                DisplayName = string.IsNullOrWhiteSpace(profile.FullName) ? profile.Username : profile.FullName,
                Username = profile.Username,
                AvatarUrl = profile.AvatarUrl,
                Followers = followerLookup.TryGetValue(profile.Id, out var followers) ? followers : 0,
                Tracks = trackLookup.TryGetValue(profile.Id, out var tracks) ? tracks : 0,
                IsFollowing = false // неважно для карусели
            });
        }

        return result;
    }

    // Новый метод для случайных треков
    private async Task<List<TrackSummary>> GetRandomTracksAsync(int count, string accessToken)
    {
        // Получаем последние N публичных треков и перемешиваем
        var recentOwners = await _supabase.GetRecentTrackOwnerIdsAsync(128, accessToken);
        var tracks = await _supabase.GetLatestTracksForUsersAsync(recentOwners, 100, accessToken);
        Shuffle(tracks);
        var selected = tracks.Take(count).ToList();

        // Получаем профили артистов
        var artistIds = selected.Select(t => t.UserId).Distinct().ToList();
        var artistProfiles = await _supabase.GetProfilesByIdsAsync(artistIds, accessToken);
        var artistLookup = artistProfiles.ToDictionary(p => p.Id);

        return selected.Select(track =>
        {
            artistLookup.TryGetValue(track.UserId, out var artistProfile);
            return new TrackSummary
            {
                TrackId = track.Id,
                Title = track.Title,
                Plays = track.PlaysCount > int.MaxValue ? int.MaxValue : (int)track.PlaysCount,
                Likes = track.LikesCount > int.MaxValue ? int.MaxValue : (int)track.LikesCount,
                Artist = artistProfile?.FullName ?? artistProfile?.Username ?? string.Empty,
                ArtistId = artistProfile?.Id ?? Guid.Empty,
                ArtistAvatar = artistProfile?.AvatarUrl,
                CoverUrl = track.CoverUrl,
                AudioUrl = track.AudioUrl,
                DurationSeconds = track.DurationSeconds
            };
        }).ToList();
    }

    private async Task<List<TrackSummary>> BuildLikesAsync(Guid userId, string accessToken)
    {
        var likes = await _supabase.GetLikedTracksAsync(userId, accessToken);
        if (likes.Count == 0)
        {
            return new List<TrackSummary>();
        }

        var artistIds = likes
            .Select(like => like.Track.UserId)
            .Distinct()
            .ToList();

        var artistProfiles = await _supabase.GetProfilesByIdsAsync(artistIds, accessToken);
        var artistLookup = artistProfiles.ToDictionary(p => p.Id);

        return likes.Select(like =>
        {
            artistLookup.TryGetValue(like.Track.UserId, out var artistProfile);
            return new TrackSummary
            {
                TrackId = like.Track.Id,
                Title = like.Track.Title,
                Plays = like.Track.PlaysCount > int.MaxValue ? int.MaxValue : (int)like.Track.PlaysCount,
                Likes = like.Track.LikesCount > int.MaxValue ? int.MaxValue : (int)like.Track.LikesCount,
                Artist = artistProfile?.FullName ?? artistProfile?.Username ?? string.Empty,
                ArtistId = artistProfile?.Id ?? Guid.Empty,
                ArtistAvatar = artistProfile?.AvatarUrl,
                CoverUrl = like.Track.CoverUrl,
                AudioUrl = like.Track.AudioUrl,
                DurationSeconds = like.Track.DurationSeconds
            };
        }).ToList();
    }

    private async Task<List<TrackSummary>> BuildHistoryAsync(Guid userId, string accessToken)
    {
        var history = await _supabase.GetListeningHistoryAsync(userId, accessToken);
        if (history.Count == 0)
        {
            return new List<TrackSummary>();
        }

        var artistIds = history
            .Where(entry => entry.Track != null)
            .Select(entry => entry.Track!.UserId)
            .Distinct()
            .ToList();

        var artistProfiles = await _supabase.GetProfilesByIdsAsync(artistIds, accessToken);
        var artistLookup = artistProfiles.ToDictionary(p => p.Id);

        return history
            .Where(entry => entry.Track != null)
            .Select(entry =>
            {
                var track = entry.Track!;
                artistLookup.TryGetValue(track.UserId, out var artistProfile);
                return new TrackSummary
                {
                    TrackId = track.Id,
                    Title = track.Title,
                    Plays = track.PlaysCount > int.MaxValue ? int.MaxValue : (int)track.PlaysCount,
                    Likes = track.LikesCount > int.MaxValue ? int.MaxValue : (int)track.LikesCount,
                    Artist = artistProfile?.FullName ?? artistProfile?.Username ?? string.Empty,
                    ArtistId = artistProfile?.Id ?? Guid.Empty,
                    ArtistAvatar = artistProfile?.AvatarUrl,
                    CoverUrl = track.CoverUrl,
                    AudioUrl = track.AudioUrl,
                    DurationSeconds = track.DurationSeconds,
                    PlayedAt = entry.PlayedAt
                };
            })
            .ToList();
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
