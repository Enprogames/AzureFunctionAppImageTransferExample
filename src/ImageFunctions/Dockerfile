# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore

WORKDIR /repo

COPY global.json Directory.Packages.props ./
COPY src/ImageFunctions/ImageFunctions.csproj src/ImageFunctions/
RUN dotnet restore src/ImageFunctions/ImageFunctions.csproj

COPY src/ImageFunctions/ src/ImageFunctions/

FROM restore AS local-publish

RUN dotnet publish src/ImageFunctions/ImageFunctions.csproj \
    -c Release \
    -o /out \
    /p:PublishAot=false \
    /p:SelfContained=false \
    /p:UseAppHost=false

FROM restore AS aot-publish

RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

RUN dotnet restore src/ImageFunctions/ImageFunctions.csproj -r linux-x64

RUN dotnet publish src/ImageFunctions/ImageFunctions.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o /out \
    /p:PublishAot=true \
    /p:InvariantGlobalization=true

# Native AOT produces the ImageFunctions executable and removes ImageFunctions.dll.
# The Functions Worker SDK metadata still points at the IL dll, so patch the
# generated host files to the native executable that exists in the publish root.
RUN sed -i \
      -e 's|"defaultExecutablePath": "dotnet"|"defaultExecutablePath": "{WorkerRoot}ImageFunctions"|' \
      -e 's|"defaultWorkerPath": "ImageFunctions.dll"|"defaultWorkerPath": "ImageFunctions"|' \
      -e 's|"workerIndexing": "true"|"workerIndexing": "false"|' \
      /out/worker.config.json \
    && sed -i \
      -e 's|"scriptFile": "ImageFunctions.dll"|"scriptFile": "ImageFunctions"|g' \
      /out/functions.metadata

# Export target used by scripts/publish-aot-zip.sh to copy the publish directory
# out of Docker without running the Functions container.
FROM scratch AS export
COPY --from=aot-publish /out /

FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0 AS runtime

ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true

COPY --from=local-publish /out /home/site/wwwroot
