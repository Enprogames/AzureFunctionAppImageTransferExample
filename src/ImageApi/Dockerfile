# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore

WORKDIR /repo

COPY global.json Directory.Packages.props ./
COPY src/ImageApi/ImageApi.csproj src/ImageApi/
RUN dotnet restore src/ImageApi/ImageApi.csproj

COPY src/ImageApi/ src/ImageApi/

FROM restore AS normal-publish

RUN dotnet publish src/ImageApi/ImageApi.csproj \
    -c Release \
    -o /out \
    /p:PublishAot=false \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=normal-publish /out ./
ENTRYPOINT ["dotnet", "ImageApi.dll"]

FROM restore AS aot-publish

RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

RUN dotnet publish src/ImageApi/ImageApi.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o /out \
    /p:PublishAot=true \
    /p:InvariantGlobalization=true

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS aot-runtime

WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=aot-publish /out ./
ENTRYPOINT ["/app/ImageApi"]
