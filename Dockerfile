FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Impulsum14/ Impulsum14/
RUN dotnet publish Impulsum14/Impulsum14.csproj -c Release --no-self-contained -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /publish/ ./
USER root
RUN mkdir -p /data /config && chown -R $APP_UID:$APP_UID /data /app/Profile
USER $APP_UID
ENV FIFA14_CONFIG=/config/server.json FIFA14_DATA_DIRECTORY=/data DOTNET_EnableDiagnostics=0
EXPOSE 42127/tcp 10000/tcp 9988/tcp
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Impulsum14.dll"]
