# Stage 1: Build React SPA
FROM node:22-alpine AS frontend
WORKDIR /app/dashboard/client
COPY dashboard/client/package*.json ./
RUN npm ci
COPY dashboard/client/ ./
RUN npm run build

# Stage 2: Build .NET
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.slnx Directory.Build.props ./
COPY src/ src/
RUN dotnet restore
RUN dotnet publish src/UltimateImapMcp.McpServer -c Release -o /app/publish

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=frontend /app/dashboard/client/dist ./wwwroot/
EXPOSE 3846 3847
ENTRYPOINT ["./UltimateImapMcp.McpServer"]
