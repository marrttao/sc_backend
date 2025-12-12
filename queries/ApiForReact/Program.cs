using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using queries.services;
using queries.models;
using Supabase;

namespace queries.ApiForReact
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Register Supabase-related services that exist in the project.
            builder.Services.AddSingleton<SupabaseService>();
            builder.Services.AddSingleton(sp => sp.GetRequiredService<SupabaseService>()._supabase);

            builder.Services.AddScoped<InsertService>();
            builder.Services.AddScoped<FetchService>();
            builder.Services.AddScoped<UpdateService>();
            builder.Services.AddScoped<DeleteService>();
            builder.Services.AddScoped<ProfileService>();
            builder.Services.AddScoped<HomeSidebarService>();
            builder.Services.AddScoped<FeedService>();

            // CORS so React can call this API
            builder.Services.AddCors(options => options.AddPolicy("Any", p =>
                p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

            var app = builder.Build();

            app.UseCors("Any");

            app.MapPost("/call", async (CallRequest request, IServiceProvider sp) =>
            {
                var service = sp.GetService(Type.GetType(request.Service)!);
                if (service == null) return Results.BadRequest("Service not found");

                var method = service.GetType().GetMethod(request.Method);
                if (method == null) return Results.BadRequest("Method not found");

                object? param = request.Parameters?.Any() == true
                    ? JsonSerializer.Deserialize(request.Parameters[0].ToString()!, method.GetParameters()[0].ParameterType)!
                    : null;

                var result = method.Invoke(service, param != null ? new[] { param } : null);

                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    if (task.GetType().IsGenericType)
                        result = task.GetType().GetProperty("Result")!.GetValue(task);
                    else
                        result = null;
                }

                return Results.Ok(result);
            });

            app.MapPost("/auth/signup", async (AuthRequest request, SupabaseService supabase, HttpContext httpContext) =>
            {
                Console.WriteLine($"[DEBUG] Received signup: email={request.Email}, username={request.Username}, fullName={request.FullName}");
                var response = await supabase.SignUpAsync(request.Email, request.Password);

                // If signup returned an access token and the client provided username/fullName,
                // try to upsert the initial profile so username/name are persisted immediately.
                try
                {
                    var payload = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[DEBUG] Signup response payload: {payload}");
                    if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(payload))
                    {
                        using var doc = JsonDocument.Parse(payload);
                        var accessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                        Console.WriteLine($"[DEBUG] Signup response contained access_token: {!string.IsNullOrWhiteSpace(accessToken)}");
                        if (!string.IsNullOrWhiteSpace(accessToken) && (!string.IsNullOrWhiteSpace(request.Username) || !string.IsNullOrWhiteSpace(request.FullName)))
                        {
                            Console.WriteLine("[DEBUG] Attempting profile upsert immediately after signup.");
                            var upsert = new ProfileUpsertRequest
                            {
                                Username = request.Username ?? string.Empty,
                                FullName = string.IsNullOrWhiteSpace(request.FullName) ? null : request.FullName,
                                AvatarUrl = null,
                                BannerUrl = null,
                                Bio = null
                            };

                            try
                            {
                                await supabase.UpsertProfileAsync(upsert, accessToken);
                                Console.WriteLine("[DEBUG] UpsertProfileAsync completed successfully during signup.");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[WARN] Failed to upsert profile during signup: {ex.Message}");
                            }
                        }
                        else
                        {
                            // If there's no access_token but the signup response contained a user id,
                            // try to upsert using the service key and that user id.
                            if ((!string.IsNullOrWhiteSpace(request.Username) || !string.IsNullOrWhiteSpace(request.FullName)) &&
                                doc.RootElement.TryGetProperty("user", out var userEl) &&
                                userEl.ValueKind == JsonValueKind.Object &&
                                userEl.TryGetProperty("id", out var idEl) &&
                                Guid.TryParse(idEl.GetString(), out var newUserId))
                            {
                                Console.WriteLine($"[DEBUG] No access_token; attempting upsert with service key for user id={newUserId}");
                                var upsert2 = new ProfileUpsertRequest
                                {
                                    Username = request.Username ?? string.Empty,
                                    FullName = string.IsNullOrWhiteSpace(request.FullName) ? null : request.FullName,
                                    AvatarUrl = null,
                                    BannerUrl = null,
                                    Bio = null
                                };
                                try
                                {
                                    await supabase.UpsertProfileWithServiceKeyAsync(newUserId, upsert2);
                                    Console.WriteLine("[DEBUG] UpsertProfileWithServiceKeyAsync completed successfully during signup.");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[WARN] Failed to upsert profile with service key during signup: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to process signup response: {ex.Message}");
                }

                return await ProxyHttpResponse(response, httpContext);
            });

            app.MapPost("/auth/signin", async (AuthRequest request, SupabaseService supabase, HttpContext httpContext) =>
            {
                var response = await supabase.SignInAsync(request.Email, request.Password);
                return await ProxyHttpResponse(response, httpContext);
            });

            app.MapPost("/auth/signout", async (HttpRequest httpRequest, SupabaseService supabase, HttpContext httpContext) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.BadRequest("Authorization header required");
                }

                var response = await supabase.SignOutAsync(token);
                return await ProxyHttpResponse(response, httpContext);
            });

            app.MapGet("/profile/me", async (HttpRequest httpRequest, ProfileService profileService, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                var profile = await profileService.BuildProfileAsync(token);
                return profile is { } ? Results.Ok(profile) : Results.NotFound();
            });

            app.MapPost("/profile/complete", async (ProfileUpsertRequest update, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                try
                {
                    await supabase.UpsertProfileAsync(update, token);
                    return Results.Ok();
                }
                catch (HttpRequestException ex)
                {
                    var status = ex.StatusCode ?? HttpStatusCode.BadRequest;
                    var message = string.IsNullOrWhiteSpace(ex.Message) ? "Unable to create playlist." : ex.Message;
                    return Results.Problem(title: message, statusCode: (int)status);
                }
            });

            app.MapGet("/profile/check-username", async (string username, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                if (string.IsNullOrWhiteSpace(username))
                {
                    return Results.BadRequest("Username is required.");
                }

                var normalized = username.Trim();
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                var existing = await supabase.GetProfileByUsernameAsync(normalized, token);
                var available = existing == null || existing.Id == user.Id;
                return Results.Ok(new { available });
            });

            app.MapGet("/profile/{username}", async (string username, HttpRequest httpRequest, ProfileService profileService, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                var profile = await profileService.BuildProfileByUsernameAsync(username, user.Id, token);
                return profile is { } ? Results.Ok(profile) : Results.NotFound();
            });

            app.MapGet("/playlists", async (HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                var playlists = await supabase.GetPlaylistsAsync(user.Id, user.Id, token, httpRequest.HttpContext.RequestAborted);
                return Results.Ok(playlists);
            });

            app.MapPost("/playlists", async (PlaylistCreateRequest request, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var playlist = await supabase.CreatePlaylistAsync(user.Id, request, token, httpRequest.HttpContext.RequestAborted);
                    return playlist is { } ? Results.Ok(playlist) : Results.BadRequest();
                }
                catch (HttpRequestException ex)
                {
                    var status = ex.StatusCode ?? HttpStatusCode.BadRequest;
                    var message = string.IsNullOrWhiteSpace(ex.Message) ? "Unable to create playlist." : ex.Message;
                    return Results.Problem(title: message, statusCode: (int)status);
                }
            });

            app.MapPatch("/playlists/{playlistId}", async (string playlistId, PlaylistUpdateRequest request, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var playlist = await supabase.UpdatePlaylistAsync(playlistId, user.Id, request, token, httpRequest.HttpContext.RequestAborted);
                    return playlist is { } ? Results.Ok(playlist) : Results.NotFound();
                }
                catch (HttpRequestException ex)
                {
                    var status = ex.StatusCode ?? HttpStatusCode.BadRequest;
                    var message = string.IsNullOrWhiteSpace(ex.Message) ? "Unable to update playlist." : ex.Message;
                    return Results.Problem(title: message, statusCode: (int)status);
                }
            });

            app.MapGet("/playlists/{playlistId}", async (string playlistId, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                var detail = await supabase.GetPlaylistDetailAsync(playlistId, user.Id, token, httpRequest.HttpContext.RequestAborted);
                return detail is { } ? Results.Ok(detail) : Results.NotFound();
            });

            app.MapPost("/playlists/{playlistId}/like", async (string playlistId, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                await supabase.LikePlaylistAsync(user.Id, playlistId, token, httpRequest.HttpContext.RequestAborted);
                return Results.Ok(new { liked = true });
            });

            app.MapDelete("/playlists/{playlistId}/like", async (string playlistId, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                await supabase.UnlikePlaylistAsync(user.Id, playlistId, token, httpRequest.HttpContext.RequestAborted);
                return Results.Ok(new { liked = false });
            });

            app.MapPost("/playlists/{playlistId}/tracks", async (string playlistId, PlaylistTrackAddRequest request, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    await supabase.AddTrackToPlaylistAsync(playlistId, user.Id, request, token, httpRequest.HttpContext.RequestAborted);
                    return Results.Ok();
                }
                catch (HttpRequestException ex)
                {
                    var status = ex.StatusCode ?? HttpStatusCode.BadRequest;
                    var message = string.IsNullOrWhiteSpace(ex.Message) ? "Unable to add track to playlist." : ex.Message;
                    return Results.Problem(title: message, statusCode: (int)status);
                }
            });

            app.MapDelete("/playlists/{playlistId}/tracks/{trackId:long}", async (string playlistId, long trackId, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    await supabase.RemoveTrackFromPlaylistAsync(playlistId, user.Id, trackId, token, httpRequest.HttpContext.RequestAborted);
                    return Results.Ok();
                }
                catch (HttpRequestException ex)
                {
                    var status = ex.StatusCode ?? HttpStatusCode.BadRequest;
                    var message = string.IsNullOrWhiteSpace(ex.Message) ? "Unable to remove track from playlist." : ex.Message;
                    return Results.Problem(title: message, statusCode: (int)status);
                }
            });

            app.MapDelete("/playlists/{playlistId}", async (string playlistId, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    await supabase.DeletePlaylistAsync(playlistId, user.Id, token, httpRequest.HttpContext.RequestAborted);
                    return Results.Ok();
                }
                catch (HttpRequestException ex)
                {
                    var status = ex.StatusCode ?? HttpStatusCode.BadRequest;
                    var message = string.IsNullOrWhiteSpace(ex.Message) ? "Unable to delete playlist." : ex.Message;
                    return Results.Problem(title: message, statusCode: (int)status);
                }
            });

            app.MapGet("/home/sidebar", async (HttpRequest httpRequest, SupabaseService supabase, HomeSidebarService sidebarService) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                var viewModel = await sidebarService.BuildSidebarAsync(user.Id, token);
                return Results.Ok(viewModel);
            });

            app.MapGet("/feed", async (HttpRequest httpRequest, SupabaseService supabase, FeedService feedService, CancellationToken cancellationToken) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                var feed = await feedService.BuildFeedAsync(user.Id, token, cancellationToken);
                return Results.Ok(feed);
            });

            app.MapGet("/search", async (HttpRequest httpRequest, SupabaseService supabase, string? q) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                if (string.IsNullOrWhiteSpace(q))
                {
                    return Results.Ok(new { profiles = new List<object>(), tracks = new List<object>() });
                }

                var profiles = await supabase.SearchProfilesAsync(q, token);
                var tracks = await supabase.SearchTracksAsync(q, token);
                
                return Results.Ok(new { profiles, tracks });
            });

            app.MapPost("/tracks/upload", async (HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                if (!httpRequest.HasFormContentType)
                {
                    return Results.BadRequest("Expected multipart/form-data payload.");
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                var form = await httpRequest.ReadFormAsync();
                var file = form.Files.GetFile("file");
                if (file == null || file.Length == 0)
                {
                    return Results.BadRequest("Audio file is required.");
                }

                var coverFile = form.Files.GetFile("coverFile");

                var rawTitle = form["title"].FirstOrDefault();
                var trackTitle = BuildTrackTitle(rawTitle, file.FileName);
                var storageFileName = BuildStorageFileName(rawTitle, file.FileName);
                var storagePath = $"{user.Id}/{storageFileName}";

                using var stream = file.OpenReadStream();
                var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

                var uploadResult = await supabase.UploadTrackAsync(stream, storagePath, contentType, token);

                var description = NormalizeOptionalString(form["description"].FirstOrDefault());
                var coverUrlInput = NormalizeOptionalString(form["coverUrl"].FirstOrDefault());
                var durationSeconds = ParseNullableInt(form["durationSeconds"].FirstOrDefault());
                var isPrivate = ParseNullableBool(form["isPrivate"].FirstOrDefault());

                string? coverPublicUrl = null;
                if (coverFile != null && coverFile.Length > 0)
                {
                    var coverFileName = BuildCoverFileName(rawTitle, coverFile.FileName);
                    var coverStoragePath = $"{user.Id}/covers/{coverFileName}";
                    using var coverStream = coverFile.OpenReadStream();
                    var coverContentType = string.IsNullOrWhiteSpace(coverFile.ContentType) ? "image/jpeg" : coverFile.ContentType;
                    var coverUploadResult = await supabase.UploadTrackAsync(coverStream, coverStoragePath, coverContentType, token);
                    coverPublicUrl = coverUploadResult.PublicUrl;
                }
                else if (!string.IsNullOrWhiteSpace(coverUrlInput))
                {
                    coverPublicUrl = coverUrlInput;
                }

                var trackPayload = new TrackInsertPayload
                {
                    UserId = user.Id,
                    Title = trackTitle,
                    Description = description,
                    AudioUrl = uploadResult.PublicUrl,
                    CoverUrl = coverPublicUrl,
                    DurationSeconds = durationSeconds,
                    IsPrivate = isPrivate
                };

                var trackRecord = await supabase.CreateTrackRecordAsync(trackPayload, token, httpRequest.HttpContext.RequestAborted);

                return Results.Ok(new { uploadResult.Path, uploadResult.PublicUrl, Track = trackRecord });
            });

            app.MapGet("/tracks/{trackId:long}", async (long trackId, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                var track = await supabase.GetTrackDetailAsync(trackId, user.Id, token, httpRequest.HttpContext.RequestAborted);
                if (track == null)
                {
                    return Results.NotFound();
                }

                var artist = await supabase.GetProfileByIdAsync(track.UserId, token);
                if (artist == null)
                {
                    return Results.NotFound();
                }

                var isLiked = await supabase.IsTrackLikedAsync(user.Id, trackId, token);
                var isFollowing = await supabase.IsFollowingAsync(user.Id, artist.Id, token);

                return Results.Ok(new TrackDetailResponse(track, artist, isLiked, isFollowing));
            });

            app.MapPost("/tracks/{trackId:long}/like", async (long trackId, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                await supabase.LikeTrackAsync(user.Id, trackId, token, httpRequest.HttpContext.RequestAborted);
                return Results.Ok(new { liked = true });
            });

            app.MapDelete("/tracks/{trackId:long}/like", async (long trackId, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                await supabase.UnlikeTrackAsync(user.Id, trackId, token, httpRequest.HttpContext.RequestAborted);
                return Results.Ok(new { liked = false });
            });

            app.MapPost("/tracks/{trackId:long}/listen", async (long trackId, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    await supabase.RecordListeningHistoryAsync(user.Id, trackId, token, httpRequest.HttpContext.RequestAborted);
                    return Results.Ok(new { recorded = true });
                }
                catch (HttpRequestException ex)
                {
                    var status = ex.StatusCode ?? HttpStatusCode.BadRequest;
                    return Results.StatusCode((int)status);
                }
            });


            app.MapPost("/profiles/{artistId:guid}/follow", async (Guid artistId, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                await supabase.FollowUserAsync(user.Id, artistId, token, httpRequest.HttpContext.RequestAborted);
                return Results.Ok(new { following = true });
            });

            app.MapDelete("/profiles/{artistId:guid}/follow", async (Guid artistId, HttpRequest httpRequest, SupabaseService supabase) =>
            {
                var token = ExtractBearerToken(httpRequest);
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Unauthorized();
                }

                var user = await supabase.GetUserAsync(token);
                if (user == null)
                {
                    return Results.Unauthorized();
                }

                await supabase.UnfollowUserAsync(user.Id, artistId, token, httpRequest.HttpContext.RequestAborted);
                return Results.Ok(new { following = false });
            });

            await app.RunAsync();
        }

        private static string? ExtractBearerToken(HttpRequest request)
        {
            var authorization = request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith("Bearer "))
            {
                return null;
            }

            return authorization.Substring(7);
        }

        private static async Task<IResult> ProxyHttpResponse(HttpResponseMessage response, HttpContext httpContext)
        {
            var payload = await response.Content.ReadAsStringAsync();
            var status = (int)response.StatusCode;
            response.Dispose();
            httpContext.Response.StatusCode = status;
            return Results.Content(payload, "application/json");
        }

        private static string BuildTrackTitle(string? title, string originalFileName)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title.Trim();
            }

            var fallback = Path.GetFileNameWithoutExtension(originalFileName);
            return string.IsNullOrWhiteSpace(fallback) ? "Untitled Track" : fallback;
        }

        private static string? NormalizeOptionalString(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }

        private static int? ParseNullableInt(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static bool? ParseNullableBool(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();

            if (bool.TryParse(trimmed, out var parsed))
            {
                return parsed;
            }

            return trimmed.ToLowerInvariant() switch
            {
                "1" => true,
                "0" => false,
                "on" => true,
                "off" => false,
                _ => null
            };
        }

        private static string BuildStorageFileName(string? title, string originalFileName)
        {
            var baseName = string.IsNullOrWhiteSpace(title)
                ? Path.GetFileNameWithoutExtension(originalFileName)
                : title.Trim();

            var normalized = baseName.Normalize(NormalizationForm.FormD);
            var filtered = normalized.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark);

            var sanitized = new string(filtered
                .Select(ch =>
                {
                    var lower = char.ToLowerInvariant(ch);
                    return (lower is >= 'a' and <= 'z') || (lower is >= '0' and <= '9') ? lower : '-';
                })
                .ToArray());

            while (sanitized.Contains("--"))
            {
                sanitized = sanitized.Replace("--", "-");
            }

            sanitized = sanitized.Trim('-');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "track";
            }

            var extension = Path.GetExtension(originalFileName);
            extension = string.IsNullOrWhiteSpace(extension) ? ".mp3" : extension.ToLowerInvariant();

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return $"{sanitized}-{timestamp}{extension}";
        }

        private static string BuildCoverFileName(string? title, string originalFileName)
        {
            var fileName = BuildStorageFileName(title, originalFileName);
            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension) || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                fileName = Path.ChangeExtension(fileName, ".jpg");
            }

            return fileName;
        }
    }

    internal record CallRequest(string Service, string Method, JsonElement[]? Parameters);
    internal record AuthRequest(string Email, string Password, string? Username, string? FullName);
}
