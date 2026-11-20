using CatCar.Contexts.CatalogInventory.Features.Execution;
    public DbSet<ExecutionJob> ExecutionJobs => Set<ExecutionJob>();
        modelBuilder.Entity<ExecutionJob>(builder =>
        {
            builder.ToTable("execution_jobs", "execution");
            builder.HasKey(x => x.WorkOrderId);
            builder.Property(x => x.WorkOrderId).ValueGeneratedNever();
            builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
            builder.Property(x => x.PartsJson).IsRequired();
            builder.Property(x => x.ServicesJson).IsRequired();
            builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
        });