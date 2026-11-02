FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble

WORKDIR /app
COPY --chown=app:app publish/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 TZ=Etc/UTC
USER app
EXPOSE 8080

ENTRYPOINT ["dotnet", "Orthanc.Server.dll"]
