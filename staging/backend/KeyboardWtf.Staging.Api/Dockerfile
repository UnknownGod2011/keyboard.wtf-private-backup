FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY KeyboardWtf.Staging.Api.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
USER $APP_UID
ENTRYPOINT ["dotnet", "KeyboardWtf.Staging.Api.dll"]
