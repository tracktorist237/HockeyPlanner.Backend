# См. статью по ссылке https://aka.ms/customizecontainer, чтобы узнать как настроить контейнер отладки и как Visual Studio использует этот Dockerfile для создания образов для ускорения отладки.

# Этот этап используется при запуске из VS в быстром режиме (по умолчанию для конфигурации отладки)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Этот этап используется для сборки проекта службы
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["HockeyPlanner.Backend.WebAPI/HockeyPlanner.Backend.WebAPI.csproj", "HockeyPlanner.Backend.WebAPI/"]
COPY ["HockeyPlanner.Backend.Application/HockeyPlanner.Backend.Application.csproj", "HockeyPlanner.Backend.Application/"]
COPY ["HockeyPlanner.Backend.Infrastructure/HockeyPlanner.Backend.Infrastructure.csproj", "HockeyPlanner.Backend.Infrastructure/"]
COPY ["HockeyPlanner.Backend.Core/HockeyPlanner.Backend.Core.csproj", "HockeyPlanner.Backend.Core/"]
COPY ["HockeyPlanner.Backend.Shared/HockeyPlanner.Backend.Shared.csproj", "HockeyPlanner.Backend.Shared/"]
RUN dotnet restore "./HockeyPlanner.Backend.WebAPI/HockeyPlanner.Backend.WebAPI.csproj"
COPY . .
WORKDIR "/src/HockeyPlanner.Backend.WebAPI"
RUN dotnet build "./HockeyPlanner.Backend.WebAPI.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Этот этап используется для публикации проекта службы, который будет скопирован на последний этап
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./HockeyPlanner.Backend.WebAPI.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Этот этап используется в рабочей среде или при запуске из VS в обычном режиме (по умолчанию, когда конфигурация отладки не используется)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Добавляем переменную окружения для порта Render
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Добавляем healthcheck для Render
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "HockeyPlanner.Backend.WebAPI.dll"]