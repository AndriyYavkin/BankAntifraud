FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY LoadGenerator.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish LoadGenerator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "LoadGenerator.dll"]
CMD ["help"]
