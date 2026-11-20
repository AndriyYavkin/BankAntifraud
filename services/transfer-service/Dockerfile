FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY TransferService.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish TransferService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8000
EXPOSE 8000
USER app
ENTRYPOINT ["dotnet", "TransferService.dll"]
