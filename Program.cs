using VectorDataBase.Services;
using VectorDataBase.Models;
using VectorDataBase.Indices;
using VectorDataBase.Embedding;

namespace SimiliVec_Explorer
{
    internal class Program
    {
        static void Main(string[] args)
        {
            VectorService vectorService = new VectorService(new HnswIndexV3
            {
                MaxNeighbours = 16,
                EfConstruction = 200,
                InverseLogM = 1.0f / MathF.Log(16)
            }, new EmbeddingModel());

            // 1. Quantum Computing & Material Science
            vectorService.AddDocument("The transition from superconducting qubits to topological qubits represents a shift toward hardware-level error correction. By utilizing Majorana fermions, researchers hope to achieve decoherence times that allow for complex chemical simulations, potentially unlocking room-temperature superconductors that would revolutionize power grids and maglev transportation.", 101);

            // 2. Synthetic Biology & Carbon Sequestration
            vectorService.AddDocument("Engineered cyanobacteria are being deployed in high-altitude bioreactors to scrub atmospheric CO2 more efficiently than natural forests. These synthetic strains are optimized for the Calvin cycle, converting carbon into stable calcium carbonate 'biolumber' used in sustainable construction, effectively turning the sky into a source of building material.", 102);

            // 3. Martian Geomorphology & Habitat Engineering
            vectorService.AddDocument("The Valles Marineris canyon system presents unique thermal pockets suitable for pressurized habitats. Using local regolith and polymer binding agents, 3D-printing drones can construct radiation-shielded domes that leverage geothermal gradients. This minimizes the need for heavy transport of shielding materials from Earth's gravity well.", 103);

            // 4. Neural Augmentation & The Link-Rate Barrier
            vectorService.AddDocument("Current Brain-Computer Interfaces (BCIs) are limited by the 'bandwidth bottleneck' of existing electrode arrays. Next-generation neural lace designs utilize flexible mesh electronics that integrate with the motor cortex, allowing for 10Gbps data transfer. This enables the direct control of complex robotic prosthetics with the same latency as biological limbs.", 104);

            // 5. Fusion Energy & Magnetic Confinement
            vectorService.AddDocument("Tokamak reactors have reached a milestone in plasma stability by utilizing AI-driven magnetic coils that adjust in micro-milliseconds. By preventing 'edge-localized modes,' these reactors maintain the 150 million degree Celsius core required for sustained deuterium-tritium fusion, providing a near-infinite source of clean, base-load energy.", 105);

            // 6. Deep Sea Mining & Robotic Autonomy
            vectorService.AddDocument("Autonomous underwater vehicles (AUVs) are now mapping the Clarion-Clipperton Zone for polymetallic nodules. Operating at depths of 5,000 meters, these units utilize SLAM algorithms to navigate silt-heavy environments. The onboard HNSW index allows them to categorize mineral density in real-time, optimizing pathfinding across the abyssal plains.", 106);

            // 7. Exoplanet Atmospheric Spectroscopy
            vectorService.AddDocument("The James Webb follow-up missions are targeting the TRAPPIST-1 system for signs of biogenic gases. By analyzing the transmission spectra of starlight passing through exoplanet atmospheres, astronomers can detect 'technosignatures' like chlorofluorocarbons, which would indicate the presence of an industrial civilization beyond our own solar system.", 107);

            // 8. Cybernetic Security & Zero-Trust Architecture
            vectorService.AddDocument("As neural implants become common, 'Cognitive Firewalls' are being developed to prevent unauthorized signal injection. These systems use behavioral biometrics to verify that incoming neural impulses originate from the user’s own biological consciousness rather than an external exploit, ensuring the integrity of the individual's sensory perception.", 108);

            // 9. Vertical Farming & Hydroponic Optimization
            vectorService.AddDocument("Hyper-local vertical farms are utilizing nutrient-film techniques controlled by precision sensors. By adjusting the LED spectrum to match specific growth phases—blue for vegetative and red for flowering—these facilities achieve a 300% increase in yield compared to traditional agriculture, while using 95% less water through closed-loop recycling.", 109);

            // 10. High-Frequency Trading & Relativistic Latency
            vectorService.AddDocument("In global financial markets, the speed of light is the ultimate limit. HFT firms are now utilizing laser-link satellite constellations to shave milliseconds off trans-Atlantic trades. Even the physical length of fiber-optic cables in data centers is calculated to the centimeter to avoid relativistic desynchronization in automated order execution.", 110);

            // 11. Semiconductor Fabrication & Quantum Tunneling Challenges
            vectorService.AddDocument("The transition to sub-2nm process nodes in semiconductor fabrication has reached a critical impasse involving quantum tunneling and extreme ultraviolet (EUV) light diffraction limits. At these scales, the behavior of electrons is no longer governed by classical drift-diffusion models but by Schrödinger wave equations. Current-generation photolithography systems must utilize multi-patterning techniques and high-numerical-aperture lenses to resolve gate features that are only dozens of atoms wide. This precision introduces a catastrophic thermal challenge: as transistor density increases, the heat flux per square millimeter exceeds that of a rocket nozzle. Engineers are now experimenting with graphene-based heat spreaders and vertically aligned carbon nanotubes to facilitate phonon transport across the silicon-die interface. Furthermore, the introduction of Gate-All-Around (GAA) field-effect transistors has required a total redesign of the source-drain epitaxy. Without these advancements in materials science, the industry faces the 'Dark Silicon' problem, where a significant portion of a chip's transistors must remain unpowered to prevent self-immolation. Future scaling will likely depend on 3D heterogeneous integration, stacking logic layers directly atop high-bandwidth memory (HBM) modules using through-silicon vias (TSVs). This architectural shift necessitates new electronic design automation (EDA) tools capable of simulating parasitic capacitance and inductance in three dimensions simultaneously. The manufacturing of such chips requires an environment cleaner than a hospital operating room, where even a single speck of dust can cause a short circuit across several thousand transistors, rendering the entire wafer useless. This document serves as a baseline for understanding the physical constraints of Moore's Law in the post-FinFET era, focusing specifically on the interplay between quantum mechanics and micro-scale thermodynamics in modern processor architecture.", 111);

            // 12
            vectorService.AddDocument("The environmental footprint of massive cloud computing clusters has led to the experimental deployment of submersible data centers located on the continental shelf. By placing server racks in pressurized, nitrogen-filled containers at depths of sixty meters, operators can leverage the surrounding ocean as a giant, passive heat sink. This eliminates the need for energy-intensive mechanical chillers and evaporative cooling towers used in traditional land-based facilities. However, the move to sub-sea environments introduces a host of novel engineering variables, most notably the impact of marine biofouling and galvanic corrosion on the outer hull integrity. Barnacles, tube worms, and various bacterial biofilms tend to accumulate on the heat exchange surfaces, significantly degrading the thermal conductivity of the container over time. To combat this, researchers are developing biomimetic coatings inspired by shark skin that disrupt the attachment mechanisms of larvae. Internally, the lack of human access requires a 99.999% reliability rate for all hardware components, as a single failed power supply unit or a leaked coolant pipe cannot be manually serviced for the duration of the five-year deployment cycle. Data transmission is handled via heavy-duty fiber optic umbilical cables that must withstand both high hydrostatic pressure and the risk of damage from commercial fishing trawlers. Furthermore, the acoustic impact of high-rpm cooling fans on the local cetacean populations is a growing area of regulatory concern. The deployment of these units marks a paradigm shift in distributed infrastructure, moving away from centralized urban hubs toward geographically isolated, environmentally integrated nodes. This long-term study analyzes the long-term structural viability of these containers against the saline chemistry of the Atlantic Ocean, while documenting the power usage effectiveness (PUE) ratios which currently approach a theoretical minimum of 1.05.", 112);

            var results = vectorService.Search("thermal management and cooling systems", 5);
            Console.WriteLine(results.Count);

            foreach (var result in results)
            {
                Console.WriteLine($" :::::::: Found document with ID: {result.Id} ::::::::: Content: {result.Content}");
            }
        }
    }
}
