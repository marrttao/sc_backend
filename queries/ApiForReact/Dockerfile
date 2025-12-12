# === 1. Билдим проект ===
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Если проект в корне — оставь как есть
COPY . .

# Если проект в подпапке (например, в 'API/' или 'src/') — укажи здесь:
# COPY ["API/*.csproj", "API/"]
# RUN dotnet restore "API/API.csproj"

RUN dotnet restore "queries.csproj"  # ← добавь имя .csproj здесь!

RUN dotnet publish "queries.csproj" -c Release -o /app/publish --no-restore

# === 2. Запускаем ===
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "sc_backend.dll"]
