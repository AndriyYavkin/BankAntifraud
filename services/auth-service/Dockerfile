FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY AuthService.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish AuthService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
RUN mkdir -p /data && chown app:app /data
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8002
EXPOSE 8002
USER app
ENTRYPOINT ["dotnet", "AuthService.dll"]
