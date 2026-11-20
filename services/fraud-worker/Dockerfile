FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY FraudWorker.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish FraudWorker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8001
EXPOSE 8001
USER app
ENTRYPOINT ["dotnet", "FraudWorker.dll"]
