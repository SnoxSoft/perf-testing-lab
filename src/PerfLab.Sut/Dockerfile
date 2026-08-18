# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Directory.Build.props .
COPY src/PerfLab.Sut/PerfLab.Sut.csproj src/PerfLab.Sut/
RUN dotnet restore src/PerfLab.Sut/PerfLab.Sut.csproj

COPY src/PerfLab.Sut/ src/PerfLab.Sut/
RUN dotnet publish src/PerfLab.Sut/PerfLab.Sut.csproj \
    -c Release \
    -o /app \
    --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Server GC is already set in Directory.Build.props. It is repeated here as an
# environment variable because the container CPU limit is what actually decides
# how many heaps the runtime creates, and a reader tuning the limit needs to see
# the two settings together.
ENV DOTNET_gcServer=1 \
    DOTNET_GCHeapHardLimitPercent=75 \
    ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "PerfLab.Sut.dll"]
