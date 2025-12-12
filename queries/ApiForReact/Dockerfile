FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем весь репозиторий
COPY . .

# Идём в подпапку с проектом и восстанавливаем пакеты
WORKDIR /src/queries/ApiForReact
RUN dotnet restore "ApiForReact.csproj"

# Билдим из той же папки
RUN dotnet publish "ApiForReact.csproj" -c Release -o /app/publish --no-restore

# Финальный этап запуска
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ApiForReact.dll"]
