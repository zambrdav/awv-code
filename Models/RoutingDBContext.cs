using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartRoutingAPI.Models.Accounts;
using SmartRoutingAPI.Models.Jobs;
using SmartRoutingAPI.Models.Routes;
using SmartRoutingAPI.Models.Vehicles;

namespace SmartRoutingAPI.Models;

/// <summary>
/// Der Datenbankkontext der SmartRouting-Anwendung.
/// </summary>
/// <remarks>
/// <para>
/// Jede Beziehung wird hier <b>ausdrücklich</b> konfiguriert. Auf die Konventionen von EF Core
/// wird nicht mehr vertraut: Genau daraus entstand der schwerwiegendste Datenmodellfehler des
/// Projekts. Weil <c>DeliveryDestination.Skills</c> ohne Gegen-Navigation deklariert war, leitete
/// EF eine 1:n-Beziehung ab und legte <c>Skills.DeliveryDestinationId</c> an – eine Fraktion
/// konnte damit nur an einer einzigen Abladestelle angenommen werden, und das Aktualisieren einer
/// Abladestelle löste die Zuordnung global. Der Fehler war in keiner Antwort sichtbar.
/// </para>
/// <para>
/// Entfallene Mengen: <c>Deliveries</c> (Typ <c>DeliveryJob</c> – fester Ablieferungsort je
/// Sammelpunkt, ohne Wirkung auf die Planung), <c>Violations</c> (VROOM-Nebenbedingungen, nie
/// beschrieben) und <c>Stats</c> (Kacheltabelle ohne jeden Schreibpfad).
/// </para>
/// </remarks>
public partial class RoutingDBContext : DbContext
{
    public RoutingDBContext(DbContextOptions<RoutingDBContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // ---- Sammelpunkte ------------------------------------------------------------------
        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasKey(k => k.Id);

            // Ein Sammelpunkt hat genau eine Fraktion; eine Fraktion hat viele Sammelpunkte.
            // Restrict statt Cascade: Das Löschen einer Fraktion darf nicht stillschweigend
            // hunderte Sammelpunkte mitnehmen.
            entity.HasOne(e => e.Skill)
                .WithMany(s => s.Jobs)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(e => e.Routes).WithMany(e => e.Jobs);

            // Der Import gleicht Sammelpunkte ausschließlich über die externe Kennung ab. Ohne
            // Index ist das ein vollständiger Tabellendurchlauf je Datensatz.
            entity.HasIndex(e => e.ExternalReference);

            // Rohdaten-Feld (üblicherweise JSON, siehe Job.Data): TEXT statt des von Pomelo aus
            // [MaxLength] abgeleiteten VARCHAR. VARCHAR-Bytes zählen bei InnoDB in die feste
            // 65.535-Byte-Zeilenobergrenze über alle Spalten hinweg; TEXT wird davon unabhängig
            // (ggf. off-page) gespeichert. Bei einem Feld, das beliebig oft ergänzt werden kann,
            // ohne dass der Server es je auswertet, ist das die robustere Wahl. Die
            // [MaxLength(Job.MaxDataLength)]-Annotation bleibt zusätzlich als Anwendungsvalidierung
            // bestehen, sodass eine zu lange Eingabe als 400 endet statt als Datenbankfehler.
            entity.Property(e => e.Data).HasColumnType("text");
        });

        // ---- Abladestellen ----------------------------------------------------------------
        modelBuilder.Entity<DeliveryDestination>(entity =>
        {
            entity.HasKey(k => k.Id);

            // Echte n:m-Beziehung mit ausdrücklich benannter Verknüpfungstabelle.
            entity.HasMany(e => e.Skills)
                .WithMany(s => s.DeliveryDestinations)
                .UsingEntity("DeliveryDestinationSkill");

            entity.HasIndex(e => e.ExternalReference);
        });

        // ---- Stammdaten -------------------------------------------------------------------
        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasKey(k => k.Id);

            // Der Anmeldename ist der Schlüssel der Authentifizierung und muss eindeutig sein.
            // Die Eindeutigkeit war bisher nur durch eine Vorabprüfung im Controller abgesichert
            // und damit für zwei gleichzeitige Anfragen nicht garantiert.
            entity.HasIndex(e => e.Username).IsUnique();
        });

        modelBuilder.Entity<Vehicle>(entity => entity.HasKey(k => k.Id));
        modelBuilder.Entity<Skill>(entity => entity.HasKey(k => k.Id));

        // ---- Routen -----------------------------------------------------------------------
        modelBuilder.Entity<Routes.Route>(entity =>
        {
            entity.HasKey(k => k.Id);

            // Listen-Endpunkte filtern auf EndDate und sortieren nach StartDate.
            entity.HasIndex(e => new { e.StartDate, e.EndDate });
        });

        modelBuilder.Entity<FrequencyRoute>(entity =>
        {
            entity.HasKey(k => k.Id);

            // TimeSpan wird in MySQL auf TIME abgebildet, das nur etwa 35 Tage abdeckt. Da
            // Wiederholungsabstände bis zu zwölf Wochen möglich sind, werden Ticks gespeichert.
            entity.Property(e => e.Frequency)
                .HasConversion(new ValueConverter<TimeSpan, long>(
                    v => v.Ticks,
                    v => TimeSpan.FromTicks(v)));
        });

        // ---- Halte einer Route ------------------------------------------------------------
        modelBuilder.Entity<Routes.Step>(entity =>
        {
            entity.HasKey(k => k.Id);

            entity.HasOne(s => s.Route)
                .WithMany(r => r.Steps)
                .HasForeignKey(s => s.RouteId)
                .OnDelete(DeleteBehavior.Cascade);

            // SetNull, nicht Cascade: Wird ein Sammelpunkt oder eine Abladestelle aus den
            // Stammdaten entfernt, bleibt die gefahrene Route als Nachweis erhalten. Für die
            // CO2-Bilanz einer Förderperiode ist das zwingend – eine Route, die beim Löschen
            // eines Containers rückwirkend verschwindet, ist kein Nachweis.
            entity.HasOne(s => s.Job)
                .WithMany()
                .HasForeignKey(s => s.JobId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(s => s.DeliveryDestination)
                .WithMany()
                .HasForeignKey(s => s.DeliveryDestinationId)
                .OnDelete(DeleteBehavior.SetNull);

            // Die Schrittliste wird ausnahmslos nach Sequence gelesen.
            entity.HasIndex(e => new { e.RouteId, e.Sequence }).IsUnique();
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);

    public virtual DbSet<DeliveryDestination> DeliveryDestinations { get; set; } = null!;
    public virtual DbSet<Job> Jobs { get; set; } = null!;
    public virtual DbSet<UserAccount> UserAccounts { get; set; } = null!;
    public virtual DbSet<Vehicle> Vehicles { get; set; } = null!;
    public virtual DbSet<Routes.Route> Routes { get; set; } = null!;
    public virtual DbSet<FrequencyRoute> FrequencyRoutes { get; set; } = null!;
    public virtual DbSet<Skill> Skills { get; set; } = null!;
    public virtual DbSet<Routes.Step> Steps { get; set; } = null!;
}
