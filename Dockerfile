FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app
COPY FBXService.csproj .
RUN dotnet restore -r linux-x64
COPY FBXService.cs .
RUN dotnet publish -c Release -o publish --no-restore -r linux-x64 --self-contained false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN adduser --disabled-password --gecos "" appuser
USER appuser
COPY --from=build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "FBXService.dll"]
