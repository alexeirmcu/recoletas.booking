FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Recoletas.Booking.slnx ./
COPY src/ ./src/
COPY doc/openapi.yaml ./doc/openapi.yaml
RUN dotnet restore src/Recoletas.Booking.API/Recoletas.Booking.API.csproj
RUN dotnet publish src/Recoletas.Booking.API/Recoletas.Booking.API.csproj \
    --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Recoletas.Booking.API.dll"]
