FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["AzureSearchEmulator/AzureSearchEmulator.csproj", "AzureSearchEmulator/"]
RUN dotnet restore "AzureSearchEmulator/AzureSearchEmulator.csproj"
COPY . .
WORKDIR "/src/AzureSearchEmulator"
RUN dotnet build "AzureSearchEmulator.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AzureSearchEmulator.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
VOLUME [ "/app/indexes" ]
ENTRYPOINT ["dotnet", "AzureSearchEmulator.dll"]
