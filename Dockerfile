# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

COPY SoteroMap.API/*.csproj ./SoteroMap.API/
RUN dotnet restore ./SoteroMap.API/SoteroMap.API.csproj

COPY SoteroMap.API/. ./SoteroMap.API/
RUN dotnet publish ./SoteroMap.API/SoteroMap.API.csproj -c Release -o /out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out .

# Carpeta para persistir el archivo .db
VOLUME /app/data

EXPOSE 80
ENTRYPOINT ["dotnet", "SoteroMap.API.dll"]
