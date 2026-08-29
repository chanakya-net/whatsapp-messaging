# syntax=docker/dockerfile:1.7

ARG DOTNET_SDK_IMAGE_DIGEST=e1fc6e423f543119c406d24e2e687d67c569f18f04a37a8b0005d80ad0dcee80
ARG DOTNET_ASPNET_IMAGE_DIGEST=207cc51496778557731c81ff670333d8ade4a4fec22768fd1be8e78474a84ecf

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:${DOTNET_SDK_IMAGE_DIGEST} AS build
ARG BUILDARCH
ARG TARGETARCH
WORKDIR /src

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl unzip \
    && case "${BUILDARCH}" in \
        amd64) protoc_arch=x86_64 ;; \
        arm64) protoc_arch=aarch_64 ;; \
        *) echo "Unsupported BUILDARCH=${BUILDARCH}" >&2 && exit 1 ;; \
       esac \
    && curl -fLsSo /tmp/protoc.zip "https://github.com/protocolbuffers/protobuf/releases/download/v28.0/protoc-28.0-linux-${protoc_arch}.zip" \
    && unzip -q /tmp/protoc.zip -d /tmp/protoc \
    && rm -rf /var/lib/apt/lists/* /tmp/protoc.zip \
    && chmod +x /tmp/protoc/bin/protoc

COPY Directory.Build.props Directory.Packages.props MessageBridge.sln ./
COPY src/MessageBridge.Application/MessageBridge.Application.csproj src/MessageBridge.Application/
COPY src/MessageBridge.Contracts/MessageBridge.Contracts.csproj src/MessageBridge.Contracts/
COPY src/MessageBridge.Domain/MessageBridge.Domain.csproj src/MessageBridge.Domain/
COPY src/MessageBridge.Infrastructure/MessageBridge.Infrastructure.csproj src/MessageBridge.Infrastructure/
COPY src/MessageBridge.Publisher/MessageBridge.Publisher.csproj src/MessageBridge.Publisher/
COPY src/MessageBridge.Publisher.EntityFrameworkCore/MessageBridge.Publisher.EntityFrameworkCore.csproj src/MessageBridge.Publisher.EntityFrameworkCore/
COPY src/MessageBridge.Worker/MessageBridge.Worker.csproj src/MessageBridge.Worker/
RUN DOTNET_NOLOGO=1 PROTOC=/tmp/protoc/bin/protoc \
    dotnet restore src/MessageBridge.Worker/MessageBridge.Worker.csproj

COPY src/ src/
RUN DOTNET_NOLOGO=1 PROTOC=/tmp/protoc/bin/protoc \
    dotnet publish src/MessageBridge.Worker/MessageBridge.Worker.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish \
    /p:UseAppHost=false

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:${DOTNET_ASPNET_IMAGE_DIGEST} AS runtime
WORKDIR /app

COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER ${APP_UID}
ENTRYPOINT ["dotnet", "MessageBridge.Worker.dll"]
