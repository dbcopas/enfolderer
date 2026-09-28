# One Dockerfile for all five services. They differ only in which project is published, so PROJECT
# selects it:
#
#   az acr build --registry $acr --image enfolderer/api:v1 `
#     --build-arg PROJECT=Enfolderer.Ai.Api -f src/Dockerfile .
#
# The build context is the repository root, because every service project has ProjectReferences
# reaching back up into src/. .dockerignore keeps the upload to the source that is actually needed.
ARG PROJECT

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT
WORKDIR /source

# Restore against the whole of src/ rather than a curated list of .csproj files. The curated list
# is the usual trick for layer caching, but it goes stale silently every time a ProjectReference is
# added, and ACR Tasks builds from a cold cache anyway.
COPY src/ src/
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
ARG PROJECT
# ENTRYPOINT cannot read a build argument, so carry it into the image as an environment variable.
ENV PROJECT=${PROJECT}
WORKDIR /app
COPY --from=build /app .

# Container Apps sends HTTP to 8080; the aspnet base image already listens there.
EXPOSE 8080

# Shell form, because the exec form cannot expand $PROJECT. `exec` matters: without it the shell
# stays as PID 1, keeps the SIGTERM that Container Apps sends when draining a revision, and the
# worker is killed mid-job instead of finishing the message it is holding.
ENTRYPOINT exec dotnet "/app/${PROJECT}.dll"
